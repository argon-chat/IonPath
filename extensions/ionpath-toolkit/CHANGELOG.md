# Change Log

All notable changes to the "ionpath-toolkit" extension will be documented in this file.

Check [Keep a Changelog](http://keepachangelog.com/) for recommendations on how to structure this file.

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