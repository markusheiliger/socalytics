# Part State Evidence Record

Development evidence for the deterministic evidence part state test. One section per measuring or confirming task.

## T001

Baseline from the repository root on Linux.

| Command | Exit code | Result |
| --- | --- | --- |
| `dotnet restore src/platform/SocAlytics.Platform.slnx` | 0 | Restored |
| `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore` | 0 | Build succeeded, 0 warnings, 0 errors |
| `docker info` | 0 | Docker daemon reachable |

Tool versions:

- .NET SDK `10.0.403`
- Docker `29.4.2`, build `055a478`
- `ffmpeg` `n8.1.3-9-g29e619e767-20260930`
- `ffprobe` `n8.1.3-9-g29e619e767-20260930`
