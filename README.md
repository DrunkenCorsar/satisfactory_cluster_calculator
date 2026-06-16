# Satisfactory Cluster Calculator

Console tool for searching Satisfactory random resource-node seeds and finding seeds where resource types are spatially clustered.

The program uses the same seed-driven node assignment logic as `satisfactory-world-generator`: node positions come from the bundled static Satisfactory world configuration, while the seed determines which resource type/purity gets assigned to each fixed position.

## What It Finds

By default, the search tracks:

- overall most clustered seed
- most clustered seed for each resource
- both `Purity unchanged` and `Purity random` scenarios

Optional least-clustered tracking can also be enabled.

Each result includes:

- seed
- score
- per-resource cluster score
- node count used for the cluster
- cluster center coordinate
- average distance to center

## Local Setup

Requirements:

- .NET SDK 10 or newer

The Satisfactory world node layout is included as static project configuration and is copied to the build output automatically.

From this folder:

```powershell
cd F:\satisfactory_cluster_calculator
dotnet build
```

Run in Release mode for realistic performance:

```powershell
dotnet run -c Release -- --max-minutes=180
```

## Resume Behavior

Searches run in time slices. Every launch must specify a runtime limit with `--max-seconds=N` or `--max-minutes=N`.

When the time slice ends, the program writes:

- a human-readable result file under `results\`, for example `results\clustered-seed-result-subset-percent-20.txt`
- a resume state file next to it, for example `results\clustered-seed-result-subset-percent-20.txt.state.json`

Run the same command again to continue from the saved offset.

The resume file is selected/validated using execution parameters such as clustering mode, subset size, seed range, least-enabled flag, and resource count.

## Common Commands

Default all-node search:

```powershell
dotnet run -c Release -- --max-minutes=180
```

Subset clustering with default 20% subset size:

```powershell
dotnet run -c Release -- --subset --max-minutes=180
```

Subset clustering with a fixed subset size of 8:

```powershell
dotnet run -c Release -- --subset --subset-count=8 --max-minutes=180
```

Enable least-clustered records:

```powershell
dotnet run -c Release -- --subset --subset-count=8 --include-least --max-minutes=180
```

Short test run:

```powershell
dotnet run -c Release -- --subset --subset-percent=20 --count=100000 --max-seconds=30
```

Intended 3-hour slice:

```powershell
dotnet run -c Release -- --subset --subset-percent=20 --max-minutes=180
```

## Parameters

`--cluster-mode=all|subset`

Selects clustering mode. `all` considers every node of each resource. `subset` considers a selected subset of each resource.

`--subset`

Shortcut for `--cluster-mode=subset`.

`--subset-percent=N`

Subset size as a percentage of each resource's node count. Default subset mode behavior is `20`. Minimum effective subset size is 4 nodes, capped by the resource's available node count.

`--subset-fraction=N`

Same as `--subset-percent`, but expressed as a fraction. Example: `--subset-fraction=0.2`.

`--subset-count=N`

Fixed subset size for every resource. Still capped by the available node count.

`--include-least`

Also computes least-clustered records. Disabled by default for performance.

`--start=N`

Signed int32 seed to start from. Default is `int.MinValue`.

`--count=N`

Number of seeds to scan. Default is the full 32-bit seed space.

`--threads=N`

Worker thread count. Default is the logical processor count.

`--max-minutes=N`

Required unless `--max-seconds` is used. Maximum runtime for this launch before saving and exiting.

`--max-seconds=N`

Required unless `--max-minutes` is used. Maximum runtime in seconds. Useful for testing resume behavior.

`--output=PATH`

Custom result file path. A `.state.json` resume file will be written next to it.

`--no-open`

Do not open the result file when the run exits.

## Output Files

If `--output` is not provided, the program chooses a parameter-specific filename:

- `results\clustered-seed-result-all-nodes.txt`
- `results\clustered-seed-result-all-nodes-with-least.txt`
- `results\clustered-seed-result-subset-percent-20.txt`
- `results\clustered-seed-result-subset-percent-20-with-least.txt`
- `results\clustered-seed-result-subset-count-8.txt`

This keeps different search modes from overwriting each other.

## Notes

- Subset mode is much more expensive than all-node mode.
- `Purity random` consumes extra RNG during generation, so resource positions can differ from `Purity unchanged`.
- Existing result/state files are safe to keep; rerunning the same command continues from the matching state file.
