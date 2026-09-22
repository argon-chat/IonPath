# ionpath.compiler

`ionc`, the IonPath compiler and language server — and an MSBuild SDK that runs it during the
build, so the generated C# never has to be committed.

## As a command-line tool

```
dotnet tool install -g ionpath.compiler
ionc compile        # in the directory that holds ion.config.json
```

## As an MSBuild SDK: C# generated at build time

Add the SDK to the project that holds `ion.config.json`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Sdk Name="ionpath.compiler" Version="x.y.z" />
  <ItemGroup>
    <PackageReference Include="ionpath.runtime.client" Version="x.y.z" />   <!-- client feature -->
    <PackageReference Include="ionpath.runtime.network" Version="x.y.z" />  <!-- server feature -->
  </ItemGroup>
</Project>
```

Several projects can share one version through `global.json`, and then drop `Version`:

```json
{ "msbuild-sdks": { "ionpath.compiler": "x.y.z" } }
```

Before compilation the build runs this package's `ionc` over the project, writes the sources to
`obj/<configuration>/<tfm>/ion/`, and compiles them. It is an SDK rather than a
`PackageReference` because NuGet does not allow a `PackageReference` to a dotnet tool package
(NU1212); the SDK form uses the very same package.

Keep the `dotnet` generator in `ion.config.json`, but without `outputs` — the build chooses the
directory, and without `outputs` `ionc compile` stops writing C# files of its own:

```json
"generators": {
  "dotnet": { "features": ["models", "client", "server"] }
}
```

Other generators (`browser`, `rust`) are unaffected and are still produced by `ionc compile`.

### Moving an existing project over

1. Add the `<Sdk>` element.
2. Remove `outputs` from the `dotnet` generator.
3. Delete what `ionc compile` generated there: `globals.cs`, `models/`, `client/`, `server/`.

If `outputs` is left in place, the build warns: `ionc compile` would keep writing sources that the
build compiles a second time.

### Diagnostics

Ion errors and warnings are reported as build errors and warnings, positioned in the `.ion`
source, so they appear in the IDE's error list and navigate to the offending line. A file that
fails to parse fails the build (the CLI only skips it).

### Properties

| Property              | Default                         | Meaning |
|-----------------------|---------------------------------|---------|
| `IonGenerateOnBuild`  | `true`                          | Turns generation off. |
| `IonProjectDirectory` | the project directory           | Directory holding `ion.config.json`; every `*.ion` below it is compiled. |
| `IonOutputDir`        | `$(IntermediateOutputPath)ion\` | Where the sources are generated. The build owns it: every `*.cs` in it is deleted before each run. |
| `IonLockMode`         | `check`                         | What the build does with `ion.lock.json` — see below. `check`, `update`, `frozen` or `none`. |
| `IonCompilerPath`     | this package's `ionc.dll`       | Another build of the compiler. |
| `IonDotnetHostPath`   | the `dotnet` running the build  | The host that runs `ionc.dll`. |

### The lock file

The build validates the schema against `ion.lock.json` but, by default, never writes it. The lock
is a ratchet — whatever it records may not be removed again — and a build runs on every save, so
recording each one would turn every half-finished edit into a baseline: add a field, save, rename
it, and the build would fail on the rename. Instead:

- **While editing** (`check`, the default): only a real break of the recorded contract fails the
  build. Anything not recorded yet can be added, renamed and removed freely; a lock that is behind
  the schema is mentioned as info (ION0071), nothing more.
- **When the change is done**, record it once and commit the lock:
  `dotnet build -p:IonLockMode=update` (or `ionc compile`). The file is only rewritten when its
  content changes, so an unchanged lock keeps its timestamp.
- **In CI**, build with `-p:IonLockMode=frozen`: it fails with ION0071 when the committed lock does
  not record the committed schema, so a contract change cannot land without its baseline.
- `none` skips lock validation altogether.

A deliberate break of the recorded contract is acknowledged the same way as before, with
`ionc lock update`.

The generator only runs when an input changed: the `.ion` files, `ion.config.json`,
`ion.lock.json`, the sources of external modules, the project file, or one of the properties above.

### Limitations

- External `modules` are resolved for type checking, but the build does not add
  `ProjectReference`s for them the way `ionc compile` patches a generated csproj; reference the
  module's project yourself.
- Requires the .NET 10 runtime (or later) on the build machine.
