# Third-Party Notices

MGsCodeMapMCP is an unofficial fork derived from
[bbajt/csharp-code-map](https://github.com/bbajt/csharp-code-map), upstream version
2.8.0 at the time this fork was created.

The upstream copyright and non-commercial open-source license are preserved verbatim
in `LICENSE.MD`. Nothing in this fork's name, documentation, or distribution implies
endorsement or official support by the original author.

The software also uses third-party .NET packages. Their names and exact resolved
versions are recorded by NuGet in the project dependency lock/assets data produced at
restore time. Each dependency remains subject to its own license. Source distributions
include the project files and `Directory.Packages.props` needed to reproduce that list.

## Material fork changes

- Visual Studio 2026/MSBuild 18 discovery and compatible Roslyn BuildHost behavior.
- Portable executable-relative configuration, data, and log locations.
- Solution-scoped baseline, cache, workspace, overlay, diff, and query routing.
- Multiple solution discovery per Git repository and automatic Git HEAD monitoring.
- Additional VB.NET and multi-solution isolation tests.
- Self-contained Windows x64 packaging documentation.
