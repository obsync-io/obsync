# Third-Party Notices

Obsync is licensed under the [MIT License](LICENSE). The Obsync installer and the
self-contained builds redistribute the third-party components below, each under its
own license. This document is provided for notice purposes and does not add
restrictions beyond the MIT License.

## Bundled Git (MinGit) — GPLv2

The Obsync MSI distributes an **unmodified** copy of Git (MinGit, from the
[Git for Windows](https://github.com/git-for-windows/git) project; the pinned version
and SHA-256 are in [packaging/build-installer.ps1](packaging/build-installer.ps1)).
Git is a separate program licensed under the
[GNU General Public License, version 2](https://github.com/git-for-windows/git/blob/main/COPYING),
not under the MIT License. Its license text and per-component notices are installed
with it at `tools\git\LICENSE.txt`. The complete corresponding source code is
available at <https://github.com/git-for-windows/git>, which satisfies the GPLv2
source-code offer for this unmodified distribution. Git is aggregated alongside
Obsync on the same installation medium ("mere aggregation" under GPLv2); it is not a
derivative work of Obsync, and Obsync is not a derivative work of Git.

## Redistributed .NET components

| Component | License | Upstream |
| --- | --- | --- |
| .NET runtime and `Microsoft.Extensions.*` libraries | MIT | <https://github.com/dotnet/runtime> |
| Microsoft.Data.SqlClient | MIT | <https://github.com/dotnet/SqlClient> |
| SQL Server Management Objects (SMO) | MIT | <https://github.com/microsoft/sqlmanagementobjects> |
| Microsoft.Data.Sqlite | MIT | <https://github.com/dotnet/efcore> |
| SQLitePCLRaw (native SQLite loader) | Apache-2.0 | <https://github.com/ericsink/SQLitePCL.raw> |
| SQLite | Public domain | <https://sqlite.org/copyright.html> |
| Dapper | Apache-2.0 | <https://github.com/DapperLib/Dapper> |
| Quartz.NET | Apache-2.0 | <https://github.com/quartznet/quartznet> |
| Octokit.NET | MIT | <https://github.com/octokit/octokit.net> |
| CommunityToolkit.Mvvm | MIT | <https://github.com/CommunityToolkit/dotnet> |
| DiffPlex | Apache-2.0 | <https://github.com/mmanela/diffplex> |
| Serilog and Serilog sinks | Apache-2.0 | <https://github.com/serilog/serilog> |
| System.CommandLine | MIT | <https://github.com/dotnet/command-line-api> |

License names above are the SPDX expressions declared by each package. Full license
texts are available at the upstream repositories linked above; the Apache License 2.0
text is at <https://www.apache.org/licenses/LICENSE-2.0>. The MIT License text, which
applies to Obsync itself and to the MIT-licensed components above (under their own
copyright holders), is:

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
