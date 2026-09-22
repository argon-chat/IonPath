# Compat schemas

`Compat.ion` is the source of the backward/forward-compatibility probe schemas. Every target
runs **`ionc`'s own output** against them, so the compat suite exercises real generated code
rather than a hand-written imitation of it.

**C#** is generated at build time: `ion.config.json` beside `Compat.ion` declares the `IonCompat`
module, and `IonTestClientServer.csproj` points the build-time generator at this directory
(`IonProjectDirectory`, see `src/ionc/Sdk`). Nothing is vendored; a codegen change reaches the
suite on the next build.

**TypeScript and Rust** are vendored. Reproduce with:

```
mkdir compat && cd compat
mkdir Contracts && cp <this dir>/Compat.ion Contracts/
cat > ion.config.json <<'JSON'
{ "name": "IonCompat", "features": ["std"],
  "generators": {
    "rust":    { "features": ["client"], "outputs": "./gen-rust",
                 "crateName": "ion-compat", "rustcorePath": "<repo>/packages/ion.rustcore" },
    "browser": { "outputFile": "./gen-ts/compat.ts", "singleFileOutput": true } } }
JSON
dotnet run --project <repo>/src/ionc/ionc.csproj -- compile --no-lock
```

Then:

| generated file          | vendored to                                                | edits |
|-------------------------|------------------------------------------------------------|-------|
| `gen-ts/compat.ts`      | `packages/ion.webcore.js/test/compat/compat.generated.ts` | import specifier `@argon-chat/ion.webcore` -> `../../src` |
| `gen-rust/src/lib.rs`   | `packages/ion.rustcore/tests/compat_schemas/mod.rs`       | dropped `pub use futures_util::StreamExt;` (futures-util is not a dev-dependency of ion-rustcore) |

## Codegen defect found while producing this

`ionc` does not escape C# keywords in field names. A field spelled `fixed:` in `.ion` emits
`public sealed record NestV1(..., IonArray<AppendedV1> fixed, ...)`, which does not compile.
The probe schema works around it by spelling the field `slots`. Out of scope here
(`src/ionc/**` is owned by another change), recorded so it is not lost.

Two more, found the same way:

* **A self-referential `msg` generates Rust that does not compile.**
  `msg DeepV1 { v: i4; child: DeepV1?; }` is accepted by the compiler and produces working C#
  and TypeScript, but Rust `pub child: Option<DeepV1>` with no `Box` is E0072, "recursive type
  has infinite size". The probe schema routes its recursion through `T[]` (`TreeV1`) instead,
  which is fine because `Vec<T>` is already indirection.

* **`Set<T>` where T is a `msg` generates Rust that does not compile.**
  The generated struct derives `Debug, Clone, PartialEq`; `HashSet<T>` needs `Eq + Hash`. C# and
  TypeScript are fine. So the portable message-bearing container positions today are `T[]`,
  `T[N]` and `Map<K,V>` — a `Set` can hold a scalar or an enum only. The probe schema uses
  `Set<TierV1>` for that position.

## The other half: what the lock file catches

`tests/golden/compat.lockprobe.sh` runs `ionc lock check` over one v1 → v2 edit per axis and
reports which ones it rejects. The results are recorded in the `lockCheck` section of
`tests/golden/compat.golden.json`. That section is where to look first: a positional array
cannot carry the information a reader would need to detect a non-append edit, so the lock file
is the only place in the toolchain that can stop one.
