#!/usr/bin/env python3
"""Compile and inspect FileOp's Copy metadata helper with a local .NET 10 SDK.

This probe is intentionally package-free and does not call any Windows API. It
compiles the actual helper source against net10.0, executes the pure metadata
mask/merge implementation, then exercises the runtime marshaller/reflection
contract that can be validated on any .NET 10 host.
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

const uint ReadOnly = 0x00000001;
const uint Hidden = 0x00000002;
const uint SystemAttribute = 0x00000004;
const uint Archive = 0x00000020;
const uint Normal = 0x00000080;
const uint Temporary = 0x00000100;
const uint Sparse = 0x00000200;
const uint ReparsePoint = 0x00000400;
const uint Compressed = 0x00000800;
const uint Offline = 0x00001000;
const uint NotContentIndexed = 0x00002000;
const uint Encrypted = 0x00004000;
const uint IntegrityStream = 0x00008000;
const uint Preserved = ReadOnly | Hidden | SystemAttribute | Archive | NotContentIndexed;
const uint DestinationOwned = Temporary | Offline;

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static uint SelectBits(uint[] bits, int selection)
{
    uint value = 0;
    for (var index = 0; index < bits.Length; index++)
    {
        if ((selection & (1 << index)) != 0)
        {
            value |= bits[index];
        }
    }
    return value;
}

static Type Nested(Type owner, string name) =>
    owner.GetNestedType(name, BindingFlags.NonPublic)
    ?? throw new InvalidOperationException($"Missing private interop type '{name}'.");

static MethodInfo Method(Type owner, string name) =>
    owner.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException($"Missing private interop method '{name}'.");

static void SequentialPack8(Type type)
{
    var layout = type.StructLayoutAttribute
        ?? throw new InvalidOperationException($"{type.Name} is missing StructLayoutAttribute.");
    Require(layout.Value == LayoutKind.Sequential,
        $"{type.Name} must use sequential layout.");
    Require(layout.Pack == 8,
        $"{type.Name} must use 8-byte packing.");
}

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
var sanitize = Method(helper, "SanitizeAttributes");
long implementationChecks = 0;
for (uint raw = 0; raw <= ushort.MaxValue; raw++)
{
    var actual = (uint)(sanitize.Invoke(null, new object[] { raw })
        ?? throw new InvalidOperationException("SanitizeAttributes returned null."));
    var expected = raw & Preserved;
    if (expected == 0)
    {
        expected = Normal;
    }
    Require(actual == expected, $"SanitizeAttributes mismatch for 0x{raw:X8}.");
    implementationChecks++;
}

uint fuzz = 0xC0FFEE01;
for (var index = 0; index < 50_000; index++)
{
    fuzz = unchecked((fuzz * 1664525u) + 1013904223u);
    var actual = (uint)(sanitize.Invoke(null, new object[] { fuzz })
        ?? throw new InvalidOperationException("SanitizeAttributes returned null."));
    var expected = fuzz & Preserved;
    if (expected == 0)
    {
        expected = Normal;
    }
    Require(actual == expected, $"SanitizeAttributes fuzz mismatch for 0x{fuzz:X8}.");
    implementationChecks++;
}

var safeBits = new[] { ReadOnly, Hidden, SystemAttribute, Archive, NotContentIndexed };
var ignoredSourceBits = new[]
{
    Normal,
    Temporary,
    Offline,
    Sparse,
    ReparsePoint,
    Compressed,
    Encrypted,
    IntegrityStream,
};
var destinationOwnedBits = new[] { Temporary, Offline };
const uint DestinationNoise = Preserved | Normal | Sparse | ReparsePoint | Compressed | Encrypted | IntegrityStream;
for (var safeSelection = 0; safeSelection < (1 << safeBits.Length); safeSelection++)
{
    var sourceSafe = SelectBits(safeBits, safeSelection);
    for (var ignoredSelection = 0; ignoredSelection < (1 << ignoredSourceBits.Length); ignoredSelection++)
    {
        var sourceIgnored = SelectBits(ignoredSourceBits, ignoredSelection);
        for (var ownedSelection = 0; ownedSelection < (1 << destinationOwnedBits.Length); ownedSelection++)
        {
            var destinationOwned = SelectBits(destinationOwnedBits, ownedSelection);
            var actual = WindowsFileCopyBasicMetadata.MergeDestinationAttributes(
                DestinationNoise | destinationOwned,
                sourceSafe | sourceIgnored);
            var expected = destinationOwned | sourceSafe;
            if (expected == 0)
            {
                expected = Normal;
            }
            Require(
                actual == expected,
                $"MergeDestinationAttributes mismatch: source=0x{(sourceSafe | sourceIgnored):X8}, destination=0x{(DestinationNoise | destinationOwned):X8}.");
            implementationChecks++;
        }
    }
}

uint mergeFuzz = 0x51A7E123;
for (var index = 0; index < 50_000; index++)
{
    mergeFuzz = unchecked((mergeFuzz * 1664525u) + 1013904223u);
    var source = mergeFuzz;
    mergeFuzz = unchecked((mergeFuzz * 1664525u) + 1013904223u);
    var destination = mergeFuzz;
    var actual = WindowsFileCopyBasicMetadata.MergeDestinationAttributes(destination, source);
    var expected = (destination & DestinationOwned) | (source & Preserved);
    if (expected == 0)
    {
        expected = Normal;
    }
    Require(
        actual == expected,
        $"MergeDestinationAttributes fuzz mismatch: source=0x{source:X8}, destination=0x{destination:X8}.");
    implementationChecks++;
}
Console.WriteLine($"PASS: actual C# metadata mask/merge implementation: {implementationChecks:N0} deterministic checks.");

var basic = Nested(helper, "FileBasicInformation");
var infoClass = Nested(helper, "FileInfoByHandleClass");
var byHandle = Nested(helper, "ByHandleFileInformation");
var fileTime = Nested(helper, "FileTime");

SequentialPack8(basic);
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

SequentialPack8(fileTime);
Require(Marshal.SizeOf(fileTime) == 8, "Unexpected FILETIME size.");
Offset(fileTime, "LowDateTime", 0);
Offset(fileTime, "HighDateTime", 4);
SequentialPack8(byHandle);
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
        empty_feed = project_dir / ".empty-feed"
        empty_feed.mkdir()

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
        run(
            [
                dotnet,
                "restore",
                "--source",
                str(empty_feed),
                "--ignore-failed-sources",
            ],
            project_dir,
            env,
        )
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
