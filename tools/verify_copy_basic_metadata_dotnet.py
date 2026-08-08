#!/usr/bin/env python3
"""Compile and inspect FileOp's Copy metadata helper with a local .NET 10 SDK.

This probe is intentionally package-free and does not call any Windows API. It
compiles the actual helper source against net10.0, then exercises the runtime
marshaller/reflection contract that can be validated on any .NET 10 host.
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import tempfile
from pathlib import Path


PROJECT = """<Project Sdk=\"Microsoft.NET.Sdk\">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>14.0</LangVersion>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
    <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
    <NuGetAudit>false</NuGetAudit>
    <UseAppHost>false</UseAppHost>
  </PropertyGroup>
</Project>
"""

PROGRAM = r'''#pragma warning disable CS0618
using System.Reflection;
using System.Runtime.InteropServices;
using FileOp.Windows.Operations;
using Microsoft.Win32.SafeHandles;

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static Type Nested(Type owner, string name) =>
    owner.GetNestedType(name, BindingFlags.NonPublic)
    ?? throw new InvalidOperationException($"Missing private interop type '{name}'.");

static MethodInfo Method(Type owner, string name) =>
    owner.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException($"Missing private interop method '{name}'.");

static void Offset(Type type, string field, int expected) =>
    Require(Marshal.OffsetOf(type, field) == new IntPtr(expected),
        $"Unexpected {type.Name}.{field} offset.");

static void Import(MethodInfo method)
{
    var dll = method.GetCustomAttribute<DllImportAttribute>()
        ?? throw new InvalidOperationException($"{method.Name} is missing DllImportAttribute.");
    Require(string.Equals(dll.Value, "kernel32.dll", StringComparison.OrdinalIgnoreCase),
        $"{method.Name} imports the wrong library.");
    Require(dll.SetLastError, $"{method.Name} must preserve Win32 last error.");
    Require(dll.CallingConvention == CallingConvention.Winapi,
        $"{method.Name} must use the platform Winapi calling convention.");
    var marshal = method.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>()
        ?? throw new InvalidOperationException($"{method.Name} return value lacks MarshalAsAttribute.");
    Require(marshal.Value == UnmanagedType.Bool,
        $"{method.Name} return value must marshal as Win32 BOOL.");
}

var helper = typeof(WindowsFileCopyBasicMetadata);
var basic = Nested(helper, "FileBasicInformation");
var infoClass = Nested(helper, "FileInfoByHandleClass");
var byHandle = Nested(helper, "ByHandleFileInformation");
var fileTime = Nested(helper, "FileTime");

Require(basic.StructLayoutAttribute?.Value == LayoutKind.Sequential,
    "FileBasicInformation must use sequential layout.");
Require(basic.StructLayoutAttribute?.Pack == 8,
    "FileBasicInformation must use 8-byte packing.");
Require(Marshal.SizeOf(basic) == 40, "Unexpected FILE_BASIC_INFO size.");
Offset(basic, "CreationTime", 0);
Offset(basic, "LastAccessTime", 8);
Offset(basic, "LastWriteTime", 16);
Offset(basic, "ChangeTime", 24);
Offset(basic, "FileAttributes", 32);
Require(infoClass.IsEnum && Enum.GetUnderlyingType(infoClass) == typeof(int),
    "FileInfoByHandleClass must be an Int32 enum.");
Require(Convert.ToInt32(Enum.Parse(infoClass, "FileBasicInfo")) == 0,
    "FileBasicInfo must remain enum value zero.");

Require(Marshal.SizeOf(fileTime) == 8, "Unexpected FILETIME size.");
Offset(fileTime, "LowDateTime", 0);
Offset(fileTime, "HighDateTime", 4);
Require(Marshal.SizeOf(byHandle) == 52, "Unexpected BY_HANDLE_FILE_INFORMATION size.");
Offset(byHandle, "FileAttributes", 0);
Offset(byHandle, "CreationTime", 4);
Offset(byHandle, "LastAccessTime", 12);
Offset(byHandle, "LastWriteTime", 20);
Offset(byHandle, "VolumeSerialNumber", 28);
Offset(byHandle, "FileSizeHigh", 32);
Offset(byHandle, "FileSizeLow", 36);
Offset(byHandle, "NumberOfLinks", 40);
Offset(byHandle, "FileIndexHigh", 44);
Offset(byHandle, "FileIndexLow", 48);

var set = Method(helper, "SetFileInformationByHandle");
Import(set);
var setParameters = set.GetParameters();
Require(set.ReturnType == typeof(bool), "SetFileInformationByHandle must return bool.");
Require(setParameters.Length == 4, "Unexpected SetFileInformationByHandle parameter count.");
Require(setParameters[0].ParameterType == typeof(SafeFileHandle), "Unexpected Set handle parameter.");
Require(setParameters[1].ParameterType == infoClass, "Unexpected Set information-class parameter.");
Require(setParameters[2].ParameterType.IsByRef && !setParameters[2].IsOut &&
        setParameters[2].ParameterType.GetElementType() == basic,
    "SetFileInformationByHandle must take ref FileBasicInformation.");
Require(setParameters[3].ParameterType == typeof(uint), "Unexpected Set buffer-size parameter.");

var get = Method(helper, "GetFileInformationByHandle");
Import(get);
var getParameters = get.GetParameters();
Require(get.ReturnType == typeof(bool), "GetFileInformationByHandle must return bool.");
Require(getParameters.Length == 2, "Unexpected GetFileInformationByHandle parameter count.");
Require(getParameters[0].ParameterType == typeof(SafeFileHandle), "Unexpected Get handle parameter.");
Require(getParameters[1].IsOut && getParameters[1].ParameterType.IsByRef &&
        getParameters[1].ParameterType.GetElementType() == byHandle,
    "GetFileInformationByHandle must take out ByHandleFileInformation.");

Console.WriteLine("PASS: actual WindowsFileCopyBasicMetadata source compiled under .NET 10 and its marshalling contract matched the expected Windows ABI.");
'''


def run(arguments: list[str], cwd: Path, env: dict[str, str]) -> None:
    subprocess.run(arguments, cwd=cwd, env=env, check=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--dotnet", help="path to a .NET 10 dotnet host; defaults to PATH")
    args = parser.parse_args()

    repo_root = args.repo_root.resolve()
    helper = repo_root / "src/FileOp.Windows/Operations/WindowsFileCopyBasicMetadata.cs"
    if not helper.is_file():
        raise FileNotFoundError(helper)

    dotnet = args.dotnet or shutil.which("dotnet")
    if not dotnet:
        raise RuntimeError("A .NET 10 SDK is required for this optional package-free interop probe.")

    version = subprocess.run(
        [dotnet, "--version"],
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    ).stdout.strip()
    if not version.startswith("10."):
        raise RuntimeError(f".NET 10 SDK required; found {version or 'unknown version'}")

    with tempfile.TemporaryDirectory(prefix="fileop-copy-metadata-dotnet-") as temp_dir:
        project_dir = Path(temp_dir)
        shutil.copy2(helper, project_dir / helper.name)
        (project_dir / "InteropProbe.csproj").write_text(PROJECT, encoding="utf-8")
        (project_dir / "Program.cs").write_text(PROGRAM, encoding="utf-8")

        env = os.environ.copy()
        env.update(
            {
                "DOTNET_CLI_HOME": str(project_dir / ".dotnet-home"),
                "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                "DOTNET_NOLOGO": "1",
                "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
                "NUGET_PACKAGES": str(project_dir / ".nuget-packages"),
            }
        )
        run([dotnet, "restore", "--ignore-failed-sources"], project_dir, env)
        run([dotnet, "build", "--configuration", "Release", "--no-restore"], project_dir, env)
        run(
            [dotnet, "run", "--configuration", "Release", "--no-build", "--no-restore"],
            project_dir,
            env,
        )

    print(f"PASS Copy basic metadata package-free .NET interop probe with SDK {version}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
