# Change Log

All notable changes to the "ionpath-toolkit" extension will be documented in this file.

Check [Keep a Changelog](http://keepachangelog.com/) for recommendations on how to structure this file.

## [Unreleased]

### Added

- `mixin` and `with` are highlighted as keywords, `mixin Audited` colours its declared name like every other declaration, and the names in a `with Audited, Traced` clause are scoped as type references
- `decimal` is highlighted as a builtin primitive, and `Map` / `Set` alongside `Maybe`, `Array` and `Partial` as builtin generics
- Syntax highlighting for all five Ion comment forms: `//`, `///`, `//!`, `/* */` and `/** */`
- Doc comments (`///`, `//!`, `/** */`) get their own `comment.*.documentation` scopes, so themes can style them apart from plain trivia
- Pressing Enter inside a `/** */` block continues it with ` * `; pressing Enter on a `///` or `//!` line continues the doc block
- Typing `/**` auto-closes with ` */`

### Removed

- The `vector` feature and the nine `vec2f`…`vec4h` builtin types are gone from both bundled grammars and from `schemas/ion-config.schema.json`. They were deleted from the language as unimplemented — no generator mapped them and no runtime defined them — so highlighting them as builtins, and accepting `"vector"` in the `features` array, both advertised something the compiler no longer has

### Fixed

- `/**/` is scoped as an ordinary block comment, and `////` (or longer) as an ordinary line comment, matching the compiler
- Quotes no longer auto-close inside comments (an apostrophe in `/// doesn't` no longer inserts a stray `'`)
- The bundled `syntaxes/ionpath.tmLanguage` plist copy was years out of date; it now matches the JSON grammar's comment, directive and type rules

### Known issue

- **Four grammar copies exist and only one ships.** `package.json` references
  `syntaxes/ionpath.tmLanguage.json` and nothing else; `syntaxes/ionpath.tmLanguage`,
  `/ionpath.tmLanguage` and `/ionpath.tmLanguage.json` at the repository root are unreferenced.
  The `mixin` / `with` / `Map` / `Set` / `decimal` rules above were added to the shipping grammar
  only, so the three copies are now behind it by exactly one language release. The two at the
  repository root already carry a `DEPRECATED duplicate` comment; the plist inside `syntaxes/`
  does not, and reads as current. **They should be deleted** — the previous release resynced them
  by hand instead, which is why they were still here to drift again.

## [1.2.0] - 2026-05-07

### Added

- Full LSP support: Hover, Go to Definition, Find References, Completions, Rename, CodeLens, Semantic Tokens
- Code Actions, Folding Ranges, Document Links (#use paths), Signature Help
- Workspace Symbols (Ctrl+T), Document Highlight, Formatting
- Project Explorer tree view in Activity Bar
- Status bar indicator for language server state
- Prerequisite checks: .NET SDK, ionc availability and version
- Commands: Restart Language Server, Show Output
- Settings: codeLens, inlayHints, minimumVersion, compilerPath

### Fixed

- File URI tracking for multi-file workspaces
- CodeLens references resolution across files

## [1.1.0]

- Initial release with syntax highlighting and basic LSP