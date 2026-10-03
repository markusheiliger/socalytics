import assert from 'node:assert/strict';
import test from 'node:test';

import { summarizeTestLog } from '../actions/run-verification/dotnet-test-summary.mjs';

const DOTNET_TEST_FAILURE_LOG = [
  'Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31, Duration: 36 s - SocAlytics.Platform.Persistence.Tests.dll (net10.0)',
  '[xUnit.net 00:02:30.45]     SocAlytics.Platform.Host.Tests.PlatformHostTests.AppHostRestarts [FAIL]',
  '  Failed SocAlytics.Platform.Host.Tests.PlatformHostTests.AppHostRestarts [2 m 29 s]',
  '  Error Message:',
  '   System.Threading.Tasks.TaskCanceledException : The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.',
  '---- System.TimeoutException : The operation was canceled.',
  '  Stack Trace:',
  '     at System.Net.Http.HttpClient.HandleFailure(Exception e, Boolean telemetryStarted)',
  '   at Aspire.Hosting.ApplicationModel.ResourceNotificationService.WatchAsync(CancellationToken cancellationToken) in /_/src/Aspire.Hosting/ResourceNotificationService.cs:line 771',
  '   at SocAlytics.Platform.Host.Tests.PlatformHostTests.WaitForHealthyEndpointAsync(HttpClient client) in /home/runner/work/socalytics/socalytics/src/platform/Tests/PlatformHostTests.cs:line 82',
  '   at SocAlytics.Platform.Host.Tests.PlatformHostTests.AppHostRestarts() in /home/runner/work/socalytics/socalytics/src/platform/Tests/PlatformHostTests.cs:line 58',
  '   at SocAlytics.Platform.Host.Tests.PlatformHostTests.AppHostRestarts() in /home/runner/work/socalytics/socalytics/src/platform/Tests/PlatformHostTests.cs:line 58',
  '--- End of stack trace from previous location ---',
  '----- Inner Stack Trace -----',
  '   at System.Net.Http.HttpConnection.SendAsync(HttpRequestMessage request) in /home/runner/work/socalytics/socalytics/src/x.cs:line 1',
  '  Standard Output Messages:',
  '   [api] Application is shutting down...',
  '  Passed SocAlytics.Platform.Host.Tests.PlatformHostTests.ServesOpenApi [1 s]',
  'Failed!  - Failed:     1, Passed:     5, Skipped:     0, Total:     6, Duration: 2 m 29 s - SocAlytics.Platform.Host.Tests.dll (net10.0)',
].join('\n');

test('summarizes failed dotnet tests with their messages, own frames, and output', () => {
  const summary = summarizeTestLog(DOTNET_TEST_FAILURE_LOG);
  assert.equal(summary, [
    'Failed test: SocAlytics.Platform.Host.Tests.PlatformHostTests.AppHostRestarts',
    'Error message:',
    '  System.Threading.Tasks.TaskCanceledException : The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.',
    '  ---- System.TimeoutException : The operation was canceled.',
    'Stack (repository frames):',
    '  at SocAlytics.Platform.Host.Tests.PlatformHostTests.WaitForHealthyEndpointAsync(HttpClient client) in src/platform/Tests/PlatformHostTests.cs:line 82',
    '  at SocAlytics.Platform.Host.Tests.PlatformHostTests.AppHostRestarts() in src/platform/Tests/PlatformHostTests.cs:line 58',
    'Test output:',
    '  [api] Application is shutting down...',
    '',
    'Test assemblies:',
    'Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31, Duration: 36 s - SocAlytics.Platform.Persistence.Tests.dll (net10.0)',
    'Failed!  - Failed:     1, Passed:     5, Skipped:     0, Total:     6, Duration: 2 m 29 s - SocAlytics.Platform.Host.Tests.dll (net10.0)',
  ].join('\n'));
});

test('summarizes build errors first, bounds the summary, and falls back to the log tail', () => {
  const build = summarizeTestLog('Restore complete\n/src/Api/Program.cs(3,1): error CS0103: The name x does not exist [/src/Api/Api.csproj]\nBuild FAILED.');
  assert.equal(build, 'Build errors:\n/src/Api/Program.cs(3,1): error CS0103: The name x does not exist [/src/Api/Api.csproj]');
  const bounded = summarizeTestLog(DOTNET_TEST_FAILURE_LOG, 120);
  assert.equal(bounded.length, 120);
  assert.match(bounded, /^Failed test: .*…$/s);
  assert.equal(summarizeTestLog('line 1\nline 2\nThe test host crashed'), 'line 1\nline 2\nThe test host crashed');
});

test('keeps test error messages short and drops embedded stack frames', () => {
  const log = [
    '  Failed Host.Tests.Restart [5 m 3 s]',
    '  Error Message:',
    "   System.OperationCanceledException : Resource 'api' failed to reach one of the target states: [Exited] before the operation was cancelled.",
    '  - Current State: Unknown',
    ...Array.from({ length: 20 }, (_, index) => `  - Health report ${index}`),
    '  at System.Net.Sockets.Socket.AwaitableSocketAsyncEventArgs.ThrowException(SocketError error)',
    '  Stack Trace:',
    '   at Host.Tests.Restart() in /home/runner/work/socalytics/socalytics/src/platform/Tests/HostTests.cs:line 63',
    'Failed!  - Failed:     1, Passed:     5, Skipped:     0, Total:     6, Duration: 5 m 3 s - Host.Tests.dll (net10.0)',
  ].join('\n');
  const summary = summarizeTestLog(log);
  const message = summary.split('\n').slice(2, summary.split('\n').indexOf('Stack (repository frames):'));
  assert.equal(message.length, 12);
  assert.match(message[0], /failed to reach one of the target states: \[Exited\]/);
  assert.doesNotMatch(summary, /AwaitableSocketAsyncEventArgs/);
  assert.match(summary, /at Host\.Tests\.Restart\(\) in src\/platform\/Tests\/HostTests\.cs:line 63/);
});
