namespace CodeMap.Core.Types;

/// <summary>
/// Strongly-typed repo-relative file path.
/// Stored in canonical form with forward slashes and no leading slash.
/// </summary>
public readonly record struct FilePath
{
    public string Value { get; }

    private FilePath(string value) => Value = value;

    /// <summary>Creates a FilePath using the repository-wide canonical path rules.</summary>
    /// <exception cref="ArgumentException">If the path is empty, absolute, or escapes the repository.</exception>
    public static FilePath From(string value) => new(RepositoryPath.CanonicalizeRelative(value));

    public override string ToString() => Value;

    /// <summary>Implicit conversion to string for serialization convenience.</summary>
    public static implicit operator string(FilePath path) => path.Value;
}
