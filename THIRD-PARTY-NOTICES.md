# Third-Party Notices

WaBiBaBuSy itself is licensed under the MIT License (see [LICENSE.txt](LICENSE.txt)).
It depends on the third-party components listed below. Each remains under its own license.

## NuGet packages

| Package | License |
|---------|---------|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Diagnostics, Avalonia.Fonts.Inter | MIT |
| CommunityToolkit.Mvvm | MIT |
| Grpc.AspNetCore, Grpc.Net.Client, Grpc.Tools | Apache-2.0 |
| Google.Protobuf | BSD-3-Clause |
| Microsoft.Extensions.* | MIT |
| Newtonsoft.Json | MIT |
| Serilog, Serilog.Extensions.Hosting, Serilog.Sinks.File | Apache-2.0 |
| System.CommandLine | MIT |
| System.Drawing.Common, System.Net.Http, System.Text.RegularExpressions | MIT |
| Makaretu.Dns.Multicast | MIT |
| Vortice.Direct2D1, Vortice.Direct3D11, Vortice.DXGI, Vortice.Mathematics | MIT |
| Magick.NET-Q8-AnyCPU, Magick.NET.SystemDrawing | Apache-2.0 |
| FFMpegCore | MIT |
| LibVLCSharp | LGPL-2.1-or-later |
| VideoLAN.LibVLC.Windows (libVLC binaries) | LGPL-2.1-or-later |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | Apache-2.0 / MIT |

### LGPL notice — libVLC

Binary distributions of WaBiBaBuSy include libVLC, which is licensed under the
GNU Lesser General Public License v2.1 or later. The corresponding source is available from
<https://www.videolan.org/vlc/download-sources.html>. libVLC is dynamically linked and can be
replaced with a compatible build.

## FFmpeg

FFmpeg is **not** distributed with this repository and is **not** required to build or run
WaBiBaBuSy. It is used only for optional video-thumbnail generation, and is looked up at runtime in
the application directory or on `PATH`.

If you install FFmpeg yourself, note that common Windows builds (including the "essentials" builds
from <https://www.gyan.dev/ffmpeg/builds/>) are configured with `--enable-gpl --enable-version3` and
are therefore covered by the **GPL v3**. Redistributing those binaries alongside this application
would place the combined distribution under the GPL. If you ship WaBiBaBuSy to others, either omit
FFmpeg or use an LGPL-configured FFmpeg build and comply with its terms.

## Windows desktop integration

Parenting a window behind the desktop icons uses the well-known `Progman` / `WorkerW` /
`SHELLDLL_DefView` technique documented in public Win32 references and community write-ups. The
`0x052C` Progman message and the `SHELLDLL_DefView` sibling walk are widely published Windows shell
behaviour, not code taken from a specific project.

## Assets

- `WaBiBaBuSy.UI/Assets/BiBaBuColorIcon.ico` — original artwork, © the BiBaBu Club.
