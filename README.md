# Roslyn Code Analysis MCP Server

## Overview
A Model Context Protocol (MCP) server that provides C# code analysis capabilities using the Roslyn compiler platform. This tool helps validate C# files, find symbol references, and perform static code analysis within the context of a .NET project.

## Features
- **Code Validation**: Analyze C# files for syntax errors, semantic issues, and compiler warnings
- **Symbol Reference Finding**: Locate all usages of a symbol across a project
- **Project Context Analysis**: Validate files within their project context
- **Code Analyzer Support**: Run Microsoft recommended code analyzers

## Tools
- `ValidateFile`: Validates a C# file using Roslyn and runs code analyzers
- `FindUsages`: Finds all references to a symbol at a specified position

## Example config
```json
{
    "servers": {
        "RoslynMCP": {
            "type": "stdio",
            "command": "dotnet",
            "args": [
                "run",
                "--no-build",
                "--project",
                "E:/Source/roslyn-mcp/RoslynMCP/RoslynMCP/RoslynMCP.csproj"
            ]
        }
    }
}
```

## Getting Started
1. Build the project
2. Run the application with:
   ```
   dotnet run
   ```
3. The server will start and listen for MCP commands via standard I/O

## Requirements
- .NET SDK
- MSBuild tools
- NuGet packages for Roslyn analyzers (automatically loaded if available)

## Example Usage
Validate a C# file:
```
ValidateFile --filePath="/path/to/your/file.cs" --runAnalyzers=true
```

Find all usages of a symbol:
```
FindUsages --filePath="/path/to/your/file.cs" --line=10 --column=15
```

## Technical Details
- Uses `Microsoft.CodeAnalysis` libraries for code analysis
- Integrates with MSBuild to load full project context
- Supports standard diagnostic analyzers
- Includes detailed output with syntax, semantic, and analyzer diagnostics
