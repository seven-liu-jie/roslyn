﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RunTests
{
    internal struct RunAllResult
    {
        internal bool Succeeded { get; }
        internal ImmutableArray<TestResult> TestResults { get; }
        internal ImmutableArray<ProcessResult> ProcessResults { get; }

        internal RunAllResult(bool succeeded, ImmutableArray<TestResult> testResults, ImmutableArray<ProcessResult> processResults)
        {
            Succeeded = succeeded;
            TestResults = testResults;
            ProcessResults = processResults;
        }
    }

    internal sealed class TestRunner
    {
        private readonly ProcessTestExecutor _testExecutor;
        private readonly Options _options;

        internal TestRunner(Options options, ProcessTestExecutor testExecutor)
        {
            _testExecutor = testExecutor;
            _options = options;
        }

        internal async Task<RunAllResult> RunAllOnHelixAsync(IEnumerable<AssemblyInfo> assemblyInfoList, CancellationToken cancellationToken)
        {
            // TODO: does having an accurate branch name matter?
            var sourceBranch = Environment.GetEnvironmentVariable("BUILD_SOURCEBRANCH");
            if (sourceBranch is null)
            {
                sourceBranch = "local";
                Environment.SetEnvironmentVariable("BUILD_SOURCEBRANCH", sourceBranch);
            }

            if (Environment.GetEnvironmentVariable("BUILD_REPOSITORY_NAME") is null)
                Environment.SetEnvironmentVariable("BUILD_REPOSITORY_NAME", "dotnet/roslyn");

            if (Environment.GetEnvironmentVariable("SYSTEM_TEAMPROJECT") is null)
                Environment.SetEnvironmentVariable("SYSTEM_TEAMPROJECT", "dnceng");

            if (Environment.GetEnvironmentVariable("BUILD_REASON") is null)
                Environment.SetEnvironmentVariable("BUILD_REASON", "pr");

            var isAzureDevOpsRun = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN") is not null;
            var correlationPayload = await makeCorrelationPayload();
            var buildNumber = Environment.GetEnvironmentVariable("BUILD_BUILDNUMBER") ?? "0";
            var workItems = assemblyInfoList.Select(ai => makeHelixWorkItemProject(ai));
            var project = @"
<Project Sdk=""Microsoft.DotNet.Helix.Sdk"" DefaultTargets=""Test"">
    <PropertyGroup>
        <HelixSource>pr/" + sourceBranch + @"</HelixSource>
        <HelixType>test</HelixType>
        <HelixBuild>" + buildNumber + @"</HelixBuild>
        <HelixTargetQueues>Windows.10.Amd64.Open</HelixTargetQueues>
        <Creator>rigibson</Creator>
        <IncludeDotNetCli>true</IncludeDotNetCli>
        <DotNetCliPackageType>sdk</DotNetCliPackageType>
        <EnableAzurePipelinesReporter>" + (isAzureDevOpsRun ? "true" : "false") + @"</EnableAzurePipelinesReporter>
    </PropertyGroup>

    <ItemGroup>
        " + correlationPayload + string.Join("", workItems) + @"
    </ItemGroup>
</Project>
";

            File.WriteAllText("helix-tmp.csproj", project);
            var process = ProcessRunner.CreateProcess(_options.DotnetFilePath, "build helix-tmp.csproj", captureOutput: true, cancellationToken: cancellationToken);
            var result = await process.Result;

            // TODO: it seems prohibitively difficult to extract and pass through meaningful results when running using a generated csproj.
            // TODO: how do we handle publishing stuff like proc dumps when test runs have crashes?
            return new RunAllResult(result.ExitCode == 0, ImmutableArray<TestResult>.Empty, ImmutableArray.Create(result));

            string makeHelixWorkItemProject(AssemblyInfo assemblyInfo)
            {
                var commandLineArguments = _testExecutor.GetCommandLineArguments(assemblyInfo);
                commandLineArguments = SecurityElement.Escape(commandLineArguments);
                var workItem = @"
        <HelixWorkItem Include=""" + assemblyInfo.DisplayName + @""">
            <Command>
                cd %HELIX_CORRELATION_PAYLOAD%
                dotnet tool restore
                dotnet pwsh ./rehydrate.ps1
                dotnet " + commandLineArguments + @"
            </Command>
        </HelixWorkItem>
";
                return workItem;
            }

            async Task<string> makeCorrelationPayload()
            {
                if (isAzureDevOpsRun)
                {
                    if (!int.TryParse(Environment.GetEnvironmentVariable("BUILD_BUILDID"), out int buildId))
                    {
                        throw new InvalidOperationException("BUILD_BUILDID environment variable must be set when running in Azure DevOps");
                    }

                    // note that we will probably always need to download the build artifact in order to do partitioning.
                    // however, we would prefer to not re-upload the artifact.
                    var artifactJson = await new HttpClient().GetStringAsync($"https://dev.azure.com/dnceng/public/_apis/build/builds/{buildId}/artifacts?artifactName=Transport_Artifacts_Windows_Debug&api-version=6.0");
                    var artifact = JsonConvert.DeserializeAnonymousType(
                        artifactJson,
                        new { resource = new { downloadUrl = "" } }
                    );
                    return $@"<HelixCorrelationPayload Include=""testPayload"">
            <Uri>{artifact.resource.downloadUrl}</Uri>
        </HelixCorrelationPayload>
";
                }
                else
                {
                    return @"<HelixCorrelationPayload Include=""$(RepoRoot)artifacts/testPayload"" />
";
                }
            }
        }

        internal async Task<RunAllResult> RunAllAsync(IEnumerable<AssemblyInfo> assemblyInfoList, CancellationToken cancellationToken)
        {
            // Use 1.5 times the number of processors for unit tests, but only 1 processor for the open integration tests
            // since they perform actual UI operations (such as mouse clicks and sending keystrokes) and we don't want two
            // tests to conflict with one-another.
            var max = _options.Sequential ? 1 : (int)(Environment.ProcessorCount * 1.5);
            var waiting = new Stack<AssemblyInfo>(assemblyInfoList);
            var running = new List<Task<TestResult>>();
            var completed = new List<TestResult>();
            var failures = 0;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var i = 0;
                while (i < running.Count)
                {
                    var task = running[i];
                    if (task.IsCompleted)
                    {
                        try
                        {
                            var testResult = await task.ConfigureAwait(false);
                            if (!testResult.Succeeded)
                            {
                                failures++;
                                if (testResult.ResultsDisplayFilePath is string resultsPath)
                                {
                                    ConsoleUtil.WriteLine(ConsoleColor.Red, resultsPath);
                                }
                                else
                                {
                                    foreach (var result in testResult.ProcessResults)
                                    {
                                        foreach (var line in result.ErrorLines)
                                        {
                                            ConsoleUtil.WriteLine(ConsoleColor.Red, line);
                                        }
                                    }
                                }
                            }

                            completed.Add(testResult);
                        }
                        catch (Exception ex)
                        {
                            ConsoleUtil.WriteLine(ConsoleColor.Red, $"Error: {ex.Message}");
                            failures++;
                        }

                        running.RemoveAt(i);
                    }
                    else
                    {
                        i++;
                    }
                }

                while (running.Count < max && waiting.Count > 0)
                {
                    var task = _testExecutor.RunTestAsync(waiting.Pop(), cancellationToken);
                    running.Add(task);
                }

                // Display the current status of the TestRunner.
                // Note: The { ... , 2 } is to right align the values, thus aligns sections into columns. 
                ConsoleUtil.Write($"  {running.Count,2} running, {waiting.Count,2} queued, {completed.Count,2} completed");
                if (failures > 0)
                {
                    ConsoleUtil.Write($", {failures,2} failures");
                }
                ConsoleUtil.WriteLine();

                if (running.Count > 0)
                {
                    await Task.WhenAny(running.ToArray());
                }
            } while (running.Count > 0);

            Print(completed);

            var processResults = ImmutableArray.CreateBuilder<ProcessResult>();
            foreach (var c in completed)
            {
                processResults.AddRange(c.ProcessResults);
            }

            return new RunAllResult((failures == 0), completed.ToImmutableArray(), processResults.ToImmutable());
        }

        private void Print(List<TestResult> testResults)
        {
            testResults.Sort((x, y) => x.Elapsed.CompareTo(y.Elapsed));

            foreach (var testResult in testResults.Where(x => !x.Succeeded))
            {
                PrintFailedTestResult(testResult);
            }

            ConsoleUtil.WriteLine("================");
            var line = new StringBuilder();
            foreach (var testResult in testResults)
            {
                line.Length = 0;
                var color = testResult.Succeeded ? Console.ForegroundColor : ConsoleColor.Red;
                line.Append($"{testResult.DisplayName,-75}");
                line.Append($" {(testResult.Succeeded ? "PASSED" : "FAILED")}");
                line.Append($" {testResult.Elapsed}");
                line.Append($" {(!string.IsNullOrEmpty(testResult.Diagnostics) ? "?" : "")}");

                var message = line.ToString();
                ConsoleUtil.WriteLine(color, message);
            }
            ConsoleUtil.WriteLine("================");

            // Print diagnostics out last so they are cleanly visible at the end of the test summary
            ConsoleUtil.WriteLine("Extra run diagnostics for logging, did not impact run results");
            foreach (var testResult in testResults.Where(x => !string.IsNullOrEmpty(x.Diagnostics)))
            {
                ConsoleUtil.WriteLine(testResult.Diagnostics!);
            }
        }

        private void PrintFailedTestResult(TestResult testResult)
        {
            // Save out the error output for easy artifact inspecting
            var outputLogPath = Path.Combine(_options.LogFilesDirectory, $"xUnitFailure-{testResult.DisplayName}.log");

            ConsoleUtil.WriteLine($"Errors {testResult.AssemblyName}");
            ConsoleUtil.WriteLine(testResult.ErrorOutput);

            // TODO: Put this in the log and take it off the ConsoleUtil output to keep it simple?
            ConsoleUtil.WriteLine($"Command: {testResult.CommandLine}");
            ConsoleUtil.WriteLine($"xUnit output log: {outputLogPath}");

            File.WriteAllText(outputLogPath, testResult.StandardOutput ?? "");

            if (!string.IsNullOrEmpty(testResult.ErrorOutput))
            {
                ConsoleUtil.WriteLine(testResult.ErrorOutput);
            }
            else
            {
                ConsoleUtil.WriteLine($"xunit produced no error output but had exit code {testResult.ExitCode}");
            }

            // If the results are html, use Process.Start to open in the browser.
            var htmlResultsFilePath = testResult.TestResultInfo.HtmlResultsFilePath;
            if (!string.IsNullOrEmpty(htmlResultsFilePath))
            {
                var startInfo = new ProcessStartInfo() { FileName = htmlResultsFilePath, UseShellExecute = true };
                Process.Start(startInfo);
            }
        }
    }
}
