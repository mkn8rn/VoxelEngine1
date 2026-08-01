# VoxelEngine1

VoxelEngine1 is a prototype .NET voxel rendering engine. It is a learning
project for a future multiplayer game engine.

The project uses the GNU Affero General Public License version 3 or later.
See [LICENSE](LICENSE) for the complete terms.

## Tests and startup benchmark

Run `dotnet test --solution MVoxelEngine1.sln` to execute all tests. The
end-to-end test opens the application and requires an OpenGL GPU.

The test loads the Default game with seed `123456`. It writes timestamped JSON
results under `TestResults/benchmarks`.

`gameLoadMilliseconds` measures the Default game data load. `buildMilliseconds`
measures the first CPU chunk render build.

`renderMilliseconds` measures the first frame that starts GPU chunk streaming.
`cameraAppearanceMilliseconds` measures the time to the first buffer swap.

`gpuStreamingStartMilliseconds` measures the time to the first chunk buffer
upload.
