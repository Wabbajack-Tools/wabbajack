﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace Wabbajack.Paths;

public enum PathFormat : byte
{
    Windows = 0,
    Unix
}

public readonly struct AbsolutePath : IPath, IComparable<AbsolutePath>, IEquatable<AbsolutePath>
{
    public static readonly AbsolutePath Empty = "".ToAbsolutePath();
    public PathFormat PathFormat { get; }


    internal readonly string[] Parts;

    public Extension Extension => Extension.FromPath(Parts[^1]);
    public RelativePath FileName => (RelativePath) Parts[^1];

    internal AbsolutePath(string[] parts, PathFormat format)
    {
        Parts = parts;
        PathFormat = format;
    }

    internal static readonly char[] StringSplits = {'/', '\\'};

    private static AbsolutePath Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return default;
        var parts = path.Split(StringSplits, StringSplitOptions.RemoveEmptyEntries);
        return new AbsolutePath(parts, DetectPathType(path));
    }

    private static readonly HashSet<char>
        DriveLetters = new("ABCDEFGJIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    private static PathFormat DetectPathType(string path)
    {
        if (path.StartsWith("/"))
            return PathFormat.Unix;
        if (path.StartsWith(@"\\"))
            return PathFormat.Windows;

        if (DriveLetters.Contains(path[0]) && path[1] == ':')
            return PathFormat.Windows;

        throw new PathException($"Invalid Path format: {path}");
    }

    public AbsolutePath Parent
    {
        get
        {
            {
                if (Parts.Length <= 1)
                    throw new PathException($"Path {this} does not have a parent folder");
                var newParts = new string[Parts.Length - 1];
                Array.Copy(Parts, newParts, newParts.Length);
                return new AbsolutePath(newParts, PathFormat);
            }
        }
    }

    public int Depth => Parts.Length;

    public AbsolutePath ReplaceExtension(Extension newExtension)
    {
        var paths = new string[Parts.Length];
        Array.Copy(Parts, paths, paths.Length);
        var oldName = paths[^1];
        var newName = RelativePath.ReplaceExtension(oldName, newExtension);
        paths[^1] = newName;
        return new AbsolutePath(paths, PathFormat);
    }

    public static explicit operator AbsolutePath(string input)
    {
        return Parse(input);
    }

    public override string ToString()
    {
        if (Parts == default) return "";
        if (PathFormat == PathFormat.Windows)
            return string.Join('\\', Parts);
        return '/' + string.Join('/', Parts);
    }

    public override int GetHashCode()
    {
        return Parts.Aggregate(0,
            (current, part) => current ^ part.GetHashCode(StringComparison.CurrentCultureIgnoreCase));
    }

    public override bool Equals(object? obj)
    {
        return obj is AbsolutePath path && Equals(path);
    }

    public int CompareTo(AbsolutePath other)
    {
        return ArrayExtensions.CompareString(Parts, other.Parts);
    }

    public bool Equals(AbsolutePath other)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
        if (other.Parts == null) return other.Parts == Parts;
        if (Parts == null) return false;
        if (Parts.Length != other.Parts.Length) return false;
        for (var idx = 0; idx < Parts.Length; idx++)
            if (!Parts[idx].Equals(other.Parts[idx], StringComparison.InvariantCultureIgnoreCase))
                return false;
        return true;
    }

    public RelativePath RelativeTo(AbsolutePath basePath)
    {
        if (!ArrayExtensions.AreEqual(basePath.Parts, 0, Parts, 0, basePath.Parts.Length))
            throw new PathException($"{basePath} is not a base path of {this}");

        var newParts = new string[Parts.Length - basePath.Parts.Length];
        Array.Copy(Parts, basePath.Parts.Length, newParts, 0, newParts.Length);
        return new RelativePath(newParts);
    }

    public bool InFolder(AbsolutePath parent)
    {
        return ArrayExtensions.AreEqual(parent.Parts, 0, Parts, 0, parent.Parts.Length);
    }

    public AbsolutePath Combine(params object[] paths)
    {
        var converted = paths.Select(p =>
        {
            return p switch
            {
                string s => (RelativePath) s,
                RelativePath path => path,
                _ => throw new PathException($"Cannot cast {p} of type {p.GetType()} to Path")
            };
        }).ToArray();
        return Combine(converted);
    }

    public AbsolutePath Combine(params RelativePath[] paths)
    {
        var newLen = Parts.Length + paths.Sum(p => p.Parts.Length);
        var newParts = new string[newLen];
        Array.Copy(Parts, newParts, Parts.Length);

        var toIdx = Parts.Length;
        foreach (var p in paths)
        {
            Array.Copy(p.Parts, 0, newParts, toIdx, p.Parts.Length);
            toIdx += p.Parts.Length;
        }

        return new AbsolutePath(newParts, PathFormat);
    }

    public static bool operator ==(AbsolutePath a, AbsolutePath b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(AbsolutePath a, AbsolutePath b)
    {
        return !a.Equals(b);
    }

    public AbsolutePath WithExtension(Extension? ext)
    {
        var parts = new string[Parts.Length];
        Array.Copy(Parts, parts, Parts.Length);
        parts[^1] = parts[^1] + ext;
        return new AbsolutePath(parts, PathFormat);
    }

    public AbsolutePath AppendToName(string append)
    {
        return Parent.Combine((FileName.WithoutExtension() + append).ToRelativePath()
            .WithExtension(Extension));
    }
}