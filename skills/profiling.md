# Profiling

How to find and fix slow C# in this plugin. It covers CPU, memory, allocations, locks, SQLite, and HTTP. Every other language is out of scope.

## When to Profile

Profile only when there is a reason. Never profile to look for something to improve.

Start when one of these is true.

* A user or a test reports that something is slow, uses too much memory, or starves playback.
* A change adds or alters a loop, a query, or a call to Jellyfin or YouTube on a path that runs for every song, such as planning a playlist import, the duplicate check, an import, the playlist fill after a scan, or the queue poll.
* A number in a scenario or a task run grew from one release to the next.

Without one of these, do not profile. Code that has no symptom and no change is not a problem.

## The Tiers

Profiling has three tiers. Start at Tier 1. Go up a tier only when the one below could not answer the question. Most issues end at Tier 1 or Tier 2.

| Tier | Answers | Budget | Needs a server |
| :--- | :--- | :--- | :--- |
| 1. Time One Method or Query | Is this code or this query slow by itself | 10 minutes | No |
| 2. Trace One Route | Which code dominates one slow route or service | 30 minutes | Yes |
| 3. Go Deeper | Why memory grows, or what a second path costs | Ask first | Yes |

* **Tier 1** is a temporary timing test and `EXPLAIN QUERY PLAN` on a small copy of `queue.db`. It needs no server and no seed.
* **Tier 2** is one CPU trace and counters of one route or service on the end to end container. Pick the one path the issue points at, and seed it once with the [recipe](#seeding-the-server).
* **Tier 3** is a heap dump, a scaled copy of the database, or a second path. Stop and ask before you start it.

### The Budget

* Each tier has a time budget. When it runs out, stop and report what you have, even when it is inconclusive.
* Profile one path per issue. Never sweep every path "to be sure".
* Stop as soon as the goal is met or the cause is proven.
* Write the tier, the path, and the stop reason in the report.

## The Rule

Measure first, change second, measure again. A change made for speed is not done until a number shows it is faster and the tests still pass.

* Never optimize from a guess. A hot path you cannot show is not a hot path.
* Never trade correctness, a safety limit, or a [security](code.md#security) rule for speed.
* Keep the simplest code that meets the goal. Stop when the goal is met.

## Goals

State the goal before you measure. A goal names the operation, the data size, and the limit.

| Good | Bad |
| :--- | :--- |
| Planning a 2,000 track library export against 50,000 library songs takes under 10 seconds | Make imports faster |
| `GET Queue` with 5,000 items answers in under 200 ms | Make the queue faster |

The plugin runs inside Jellyfin on the user's server. Treat its CPU, memory, and disk as shared. A task that is fast but starves playback has failed.

## Check These First

These causes are common. Check each against the code before you measure anything.

| Check | Usual cause |
| :--- | :--- |
| A loop that asks Jellyfin or SQLite the same question for every track | The answer does not change inside a run, so ask once per run |
| A library query that reads every song for each track of an export | Build one lookup of the library per import |
| A catalog or YouTube search repeated for the same track | Cache the answer for the length of one run |
| A queue read that loads every column for the poll | The payload is larger than the page needs |
| A log string built in a hot loop | The string is built even when the level is off |

If the code shows the cause, a Tier 1 test is enough to prove it.

## Seeding the Server

Tier 2 needs data that is big enough to show the problem and no bigger.

* Start the end to end container with `python3 e2e/docker_e2e.py --keep`, which leaves its server, library, and queue in place on port 8097.
* Add a library export or library songs only as large as the goal names, and scan once. Never reseed between runs.
* Write down the data size in the report.
* Never build a bigger seed to prove a scaling claim. Copy `queue.db` out and scale the copy at Tier 3.

## Tools

Use only the tools Microsoft ships for .NET. They need the .NET 10 SDK, the same as the repo, so check it first.

```bash
dotnet --list-sdks
```

Install the tools once. Update them with `dotnet tool update --global` and the same name.

```bash
dotnet tool install --global dotnet-trace
```

```bash
dotnet tool install --global dotnet-counters
```

```bash
dotnet tool install --global dotnet-gcdump
```

The tools install to `~/.dotnet/tools`. On macOS that folder can be missing from `PATH`, because the dotnet installer writes the literal text `~/.dotnet/tools` to `/etc/paths.d`, and a shell does not expand it. Add this line to `~/.zshrc` and open a new shell.

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
```

Check that each tool runs.

```bash
dotnet-trace --version
```

| Question | Tool |
| :--- | :--- |
| Which methods use the CPU | `dotnet-trace` |
| Is the process healthy over time | `dotnet-counters` |
| What holds the memory | `dotnet-gcdump` |
| How fast is this one method | A temporary test with `Stopwatch` and `GC.GetAllocatedBytesForCurrentThread()` |
| How long did this run take | A `Stopwatch` in a temporary test, or `dotnet test` timing |
| Which SQL is slow | `EXPLAIN QUERY PLAN` and a timed test against a real SQLite file |

A trace is a `.nettrace` file, which `dotnet-trace` writes by default. Open it in PerfView on Windows, or turn it into a speedscope file and open that at [speedscope.app](https://www.speedscope.app). Pass `--format Speedscope` to `collect` to get both files in one step, or convert a trace later.

```bash
dotnet-trace convert run.nettrace --format Speedscope -o run
```

This writes `run.speedscope.json`. Keep every trace, dump, and test output outside the repository.

## Tier 1: Time One Method or Query

A method with no I/O is best measured alone. For a query, run `EXPLAIN QUERY PLAN` on a copy of `queue.db`, as in the last step of Tier 2, and time the query in a test against a real SQLite file.

* Write a temporary xUnit test in the test project that times the method with `Stopwatch` and reads `GC.GetAllocatedBytesForCurrentThread()` before and after. Take the difference on the same thread, since the counter covers only the current thread. Write the numbers with `ITestOutputHelper`, and read them with `--logger "console;verbosity=detailed"`. Never commit the test.
* Build and run in `Release`, with `dotnet test -c Release`, and without a debugger.
* Warm up first. Call the method a few times before you measure, so the JIT is not in the number.
* Run it many times and take the median. One run proves nothing.
* Run it before and after the change, on the same machine, with nothing else busy.
* Give it realistic input. A list of three items hides what a list of 50,000 shows.
* Never commit the test, its output, or any trace.

To see where the time goes inside the method, trace the test host. Make the test loop for at least 10 seconds, so a trace has time to attach. Do not use `dotnet-trace collect -- dotnet test`. It traces the `dotnet test` command and not the test code, and it can hang. Run the tests in one shell and attach from another.

```bash
dotnet test -c Release --filter FullyQualifiedName~MyTimingTest
```

Find the test host process id, which is the newest `testhost` process.

```bash
pgrep -f testhost
```

```bash
dotnet-trace collect --process-id <pid> --duration 00:00:08 --format Speedscope -o ~/Desktop/method.nettrace
```

For what holds the memory, take a heap dump of the same process, and read it.

```bash
dotnet-gcdump collect --process-id <pid> -o ~/Desktop/method.gcdump
```

```bash
dotnet-gcdump report ~/Desktop/method.gcdump
```

## Tier 2: Trace One Route

The plugin runs in Jellyfin's process, so attach to that process. Use the end to end container so the data is real and the run repeats. Trace one route or service, the one the issue points at.

The Jellyfin image is Linux and runs Jellyfin as process 1, as root. It is self contained, so it has no `dotnet` and no diagnostic tools. A tool on the Mac cannot reach a process inside the Docker VM, so the tool must run inside the container, from the same `/tmp` as Jellyfin.

1. Start the container and leave it running. It builds the plugin in Release.

    ```bash
    e2e/run.sh --keep
    ```

2. Install the tools in the container `yta-e2e`. Use the standalone Linux builds that match the container CPU, which `docker exec yta-e2e uname -m` prints. Use `linux-arm64` for `aarch64` and `linux-x64` for `x86_64` in the address.

    ```bash
    docker exec yta-e2e curl -sL -o /tmp/dotnet-trace https://aka.ms/dotnet-trace/linux-arm64
    ```

    ```bash
    docker exec yta-e2e chmod +x /tmp/dotnet-trace
    ```

    Repeat both for `dotnet-counters` and `dotnet-gcdump`. These builds are not self contained, so unpack a .NET 8 runtime beside them and point `DOTNET_ROOT` at it on every call.

    ```bash
    docker exec yta-e2e sh -c 'mkdir -p /tmp/dotnet-rt && curl -sL https://aka.ms/dotnet/8.0/dotnet-runtime-linux-arm64.tar.gz | tar -xz -C /tmp/dotnet-rt'
    ```

3. Record a CPU trace of Jellyfin, which is process 1, while the slow route runs. Use the `dotnet-sampled-thread-time` profile, since the default one makes a trace of 100 MB or more.

    ```bash
    docker exec -e DOTNET_ROOT=/tmp/dotnet-rt yta-e2e /tmp/dotnet-trace collect --process-id 1 --profile dotnet-sampled-thread-time --duration 00:01:00 -o /tmp/run.nettrace
    ```

    Wait for the tool to exit before copying the file. A trace copied while the tool still runs has no method names.
4. Copy the trace out to a folder outside the repo, then convert it on the Mac.

    ```bash
    docker cp yta-e2e:/tmp/run.nettrace ~/Desktop/run.nettrace
    ```

    ```bash
    dotnet-trace convert ~/Desktop/run.nettrace --format Speedscope -o ~/Desktop/run
    ```

5. Read the `.speedscope.json` file in speedscope. An idle thread shows as `UNMANAGED_CODE_TIME`, and native work such as SQLite, TagLib file writes, and the yt-dlp process wait shows there too, so credit it to the nearest plugin frame above it.
6. For counters, record while the route runs, and read allocation, collections, thread pool queue length, and lock contentions.

    ```bash
    docker exec -e DOTNET_ROOT=/tmp/dotnet-rt yta-e2e /tmp/dotnet-counters collect --process-id 1 --counters System.Runtime --format json --duration 00:01:00 -o /tmp/counters
    ```

7. To time a route, send an API key in an `Authorization: MediaBrowser Token="<key>"` header.

    ```bash
    curl -s -o /dev/null -w "%{time_total}" -H 'Authorization: MediaBrowser Token="<key>"' "http://localhost:8097/YouTubeAudio/Queue"
    ```

8. To read the query plans, copy the whole data folder out, since the database runs in WAL mode, and open the copy only.

    ```bash
    docker cp yta-e2e:/config/data/youtubeaudio/. ~/Desktop/dbcopy
    ```

A tool inside the container is lost when the container is recreated, so install it again after the next run.

## Tier 3: Go Deeper

Stop and ask before you start Tier 3. Say what Tier 2 left unanswered and what you will do.

* Take a heap dump in the middle of the run and after it, per the memory step of Tier 2.
* Scale a copy of `queue.db` to the size the issue needs, and run the queries against the copy. Never open the live database.
* Trace a second path only when the first trace points at it.

## Reading a Trace

* **Inclusive time** is a method plus everything it calls. **Exclusive time** is the method alone. Start from the largest inclusive plugin frame and walk down to the largest exclusive one.
* A frame in `System.Threading` or a wait is time spent blocked. Find what it waits on, since more CPU will not help.
* Many small frames in the allocator or the GC point to allocation pressure. Read the allocation counters, then find the allocating call.
* Time spent in a `Task` that never shows CPU is waiting on I/O. Count the calls before you tune one call.

## Common Fixes

Try these in order. Each is cheaper and safer than the next.

1. **Do less.** Skip a track that is already in the library or the queue. Stop a pass at the first answer.
2. **Do it once.** Hoist work out of a loop. Cache a result for the length of one run, never across runs unless it is invalidated.
3. **Do it in bulk.** One query for a set, not one per row. One transaction for a batch of writes. One page, not the whole table.
4. **Do it together.** Run independent calls with `ConcurrentTaskRunner` from Base, with a limit that respects YouTube and Apple's catalog.
5. **Allocate less.** Use `Span<T>`, `StringBuilder`, pooled buffers, and `static` lambdas where a trace shows allocation pressure. Never do this without a number.
6. **Stream, never buffer.** Read a file or a response as a stream. Never load a large file into memory.

### SQLite

* Run `EXPLAIN QUERY PLAN` on any query that reads a large table. A `SCAN` of a big table needs an index or a different query.
* An index is a schema change. It is a [stop and ask](../starthere.md#stop-and-ask) item.
* Batch writes in one transaction, and keep a transaction short so it does not block readers.
* Select only the columns you use.

### Async and Threads

* Never block on a task with `.Result` or `.Wait()`. It starves the thread pool.
* Pass the `CancellationToken` through, so a stopped run stops working.
* Hold a lock for the shortest time, and never across an `await`.
* Bound every queue and every degree of parallelism.

### HTTP

* Reuse `HttpClient`s from `IHttpClientFactory` or a static instance. Never build a new one per call.
* Keep the pause `AppleCatalogClient` holds between catalog requests. Never remove it for speed.
* Never loosen the redirect refusal or a checksum check for speed.
* Copy a stream with Base's `StreamUtilities`. Never write a copy loop or a stall check by hand.

### Logging

* Use the `ILogger` templates in [code.md](code.md#c-conventions). Never build the message with string interpolation.
* A log in a hot loop costs even when the level is off. Move it out of the loop or guard it with `IsEnabled`.

## Proving the Result

1. Measure the same scenario before and after, with the same data and the same machine. Record both numbers.
2. Run the tests for every layer the change touches, per [testing.md](testing.md#layers).
3. For a change to how songs download or reach the library, run the end to end test. A faster wrong answer is a bug.
4. Check that memory and the thread count stay flat across a long run.
5. Report the numbers in the commit message or the report, as before and after for the same operation.

## What Not to Do

* Never ship a profiler, a timing test, a `Stopwatch`, an allocation counter, or a timing log in the plugin.
* Never commit trace files, gcdumps, or timing output.
* Never raise a timeout or lower a retry count to hide a slow call.
* Never remove a duplicate check, a checksum check, or a guard to gain speed.
* Never profile a Debug build. Use `Release`.
* Never change a [stop and ask](../starthere.md#stop-and-ask) item without approval, even when it is the cause.

## Checklist

* [ ] The reason to profile is a symptom, a change to a hot path, or a number that grew.
* [ ] The tier, the path, and the stop reason are in the report.
* [ ] The goal names the operation, the data size, and the limit.
* [ ] A trace, a counter, or a timed test shows where the time or memory goes.
* [ ] The fix is the simplest one that meets the goal.
* [ ] Before and after numbers exist for the same operation and data.
* [ ] A `Release` build was measured.
* [ ] The tests for every touched layer pass, and the end to end test passes when it applies.
* [ ] No profiler code, trace file, timing test, or output is in the diff.
* [ ] Anything on the stop and ask list, such as an index, was approved first.
