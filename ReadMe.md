# Multi-column advanced DIR-like file listing for PowerShell. 
Displays files and folders in multiple columns (like DIR /w), automatically taking into account the width of the console window. 

## Usage
`dirw [path and pattern] [-l|-long] [-s|-size] [-c|-cachedSize] [-a|-all] [-sn|-ss|-sd] [-oa|-od] [-h|-help]`

## Options
| Option | Description |
| :--- | :--- |
| `path/pattern` | Folder or wildcard pattern (default: `.`) |
| `-l`, `-long` | Show sizes and dates (long format) |
| `-s`, `-size` | Calculate size of folders (implies `-l`) |
| `-c`, `-cachedSize` | **High Performance**: Use in-process DLL for cached folder sizes (implies `-s`) |
| `-a`, `-all` | Show hidden files and folders |
| `-sn`, `-sortName` | Sort by name (default) |
| `-ss`, `-sortSize` | Sort by size |
| `-sd`, `-sortDate` | Sort by date |
| `-oa`, `-orderAscending` | Order ascending (default) |
| `-od`, `-orderDescending` | Order descending |
| `-h`, `-help` | Show help |

## Fast Folder Sizes (sizew)
To enable near-instant folder sizes via caching, you need to compile the native `sizew` utility. Run the following command once after installing the module:

```powershell
Install-SizeWBinaries
```

### High-Performance DLL Integration
When you use `dirw -c`, the module loads a high-performance C# library (`sizew.dll`) directly into the PowerShell process. This eliminates the overhead of creating new processes, making calculations **10x faster** than traditional methods.

### native `sizew` cmdlet
The module also exports a native `sizew` command that you can use directly:
- `sizew -r` : Recursive scan of the current directory.
- `sizew -rc C:\Windows` : Force recalculate sizes for a specific folder.
- `sizew -raw C:\Temp` : Output raw size in bytes.
- `sizew -h` : Show help.

## Examples
- `dirw` : Show all files and folders in the current directory.
- `dirw -c` : Show with folder sizes using high-speed DLL cache.
- `dirw *.ps1 -sd -od` : Show .ps1 files sorted by date, newest first.
- `dirw C:\temp -l -ss` : Show all files in `C:\temp\` in long format, sorted by size.
- `dirw C:\windows\ -s -a -od` : Show all items (including hidden) with calculated folder sizes, sorted largest to smallest.