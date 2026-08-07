using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FabrCore.Core.Skills;
using FabrCore.Sdk;

namespace OpenCaddis.Server.Builder;

internal static class AddonBuilderSkillPackages
{
    private const string ResourcePrefix = "OpenCaddis.Server.Builder.AgentSkills/";
    private const int PackagedDescriptionMaxLength = 1000;
    private static readonly Lazy<IReadOnlyList<EmbeddedSkillPackage>> Packages =
        new(LoadPackages, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<string> References =>
        Packages.Value.Select(package => package.Reference).ToArray();

    public static IReadOnlyList<EmbeddedSkillPackage> GetPackages() => Packages.Value;

    public static async Task<IReadOnlyList<string>> PublishAsync(
        IFabrCoreHostApiClient apiClient,
        string principalHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        return await PublishAsync(
            (principal, name, version, stream, token) => apiClient.PublishHarnessSkillAsync(
                principal,
                name,
                version,
                stream,
                token),
            principalHandle,
            cancellationToken);
    }

    internal static async Task<IReadOnlyList<string>> PublishAsync(
        Func<string, string, string, Stream, CancellationToken, Task<FabrCoreSkillPublishResult>> publish,
        string principalHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalHandle);

        foreach (var package in Packages.Value)
        {
            await using var zipStream = package.OpenRead();
            FabrCoreSkillPublishResult result;
            try
            {
                result = await publish(
                    principalHandle,
                    package.Name,
                    package.Version,
                    zipStream,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Could not publish FabrCore Harness Skill '{package.Reference}': {exception.Message}",
                    exception);
            }

            if (!string.Equals(result.Manifest.Name, package.Name, StringComparison.Ordinal) ||
                !string.Equals(result.Manifest.Version, package.Version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"FabrCore published '{result.Manifest.Name}@{result.Manifest.Version}' while " +
                    $"'{package.Reference}' was requested.");
            }
        }

        return References;
    }

    private static IReadOnlyList<EmbeddedSkillPackage> LoadPackages()
    {
        var assembly = typeof(AddonBuilderSkillPackages).Assembly;
        var packages = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                           name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => LoadPackage(assembly, name))
            .ToArray();
        if (packages.Length == 0)
        {
            throw new InvalidOperationException(
                "No embedded Agent Skill packages were found in OpenCaddis.Server.Builder.");
        }

        var duplicate = packages
            .GroupBy(package => package.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"The embedded FabrCore Agent Skill package '{duplicate.Key}' is duplicated.");
        }

        return packages;
    }

    private static EmbeddedSkillPackage LoadPackage(Assembly assembly, string resourceName)
    {
        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded skill resource '{resourceName}' could not be opened.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var content = NormalizePackage(buffer.ToArray());
        var name = resourceName[ResourcePrefix.Length..^".zip".Length];
        if (!FabrCoreSkillReference.IsValidSkillName(name))
        {
            throw new InvalidOperationException($"Embedded skill resource '{resourceName}' has an invalid name.");
        }

        var version = ComputeContentVersion(content);
        return new EmbeddedSkillPackage(name, version, content);
    }

    private static byte[] NormalizePackage(byte[] zipContent)
    {
        using var source = new ZipArchive(
            new MemoryStream(zipContent, writable: false),
            ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var destination = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var sourceEntry in source.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            {
                var destinationEntry = destination.CreateEntry(sourceEntry.FullName, CompressionLevel.Optimal);
                destinationEntry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var destinationStream = destinationEntry.Open();
                using var sourceStream = sourceEntry.Open();
                if (!string.Equals(sourceEntry.FullName, "SKILL.md", StringComparison.Ordinal))
                {
                    sourceStream.CopyTo(destinationStream);
                    continue;
                }

                using var reader = new StreamReader(
                    sourceStream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
                var markdown = NormalizeDescription(reader.ReadToEnd());
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(markdown);
                destinationStream.Write(bytes);
            }
        }

        return output.ToArray();
    }

    private static string NormalizeDescription(string markdown)
    {
        var newline = markdown.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var descriptionStart = lines.FindIndex(line =>
            line.StartsWith("description:", StringComparison.Ordinal));
        if (descriptionStart < 0)
        {
            return markdown;
        }

        var descriptionEnd = descriptionStart + 1;
        while (descriptionEnd < lines.Count &&
               (string.IsNullOrWhiteSpace(lines[descriptionEnd]) ||
                char.IsWhiteSpace(lines[descriptionEnd][0])))
        {
            descriptionEnd++;
        }

        var firstValue = lines[descriptionStart]["description:".Length..].Trim();
        var parts = new List<string>();
        if (firstValue is not "" and not ">" and not ">-" and not "|" and not "|-")
        {
            parts.Add(firstValue.Trim('"', '\''));
        }

        parts.AddRange(lines
            .Skip(descriptionStart + 1)
            .Take(descriptionEnd - descriptionStart - 1)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));
        var description = Regex.Replace(string.Join(' ', parts), "\\s+", " ").Trim();
        if (description.Length > PackagedDescriptionMaxLength)
        {
            var candidate = description[..PackagedDescriptionMaxLength];
            var wordBoundary = candidate.LastIndexOf(' ');
            description = (wordBoundary > PackagedDescriptionMaxLength / 2
                ? candidate[..wordBoundary]
                : candidate).TrimEnd() + ".";
        }

        lines.RemoveRange(descriptionStart, descriptionEnd - descriptionStart);
        lines.Insert(descriptionStart, $"  {description}");
        lines.Insert(descriptionStart, "description: >");
        return string.Join(newline, lines);
    }

    private static string ComputeContentVersion(byte[] zipContent)
    {
        using var archive = new ZipArchive(new MemoryStream(zipContent, writable: false), ZipArchiveMode.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var entry in archive.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            var path = Encoding.UTF8.GetBytes(entry.FullName);
            BinaryPrimitives.WriteInt32BigEndian(length, path.Length);
            hash.AppendData(length);
            hash.AppendData(path);

            using var input = entry.Open();
            var fileContent = new MemoryStream();
            input.CopyTo(fileContent);
            BinaryPrimitives.WriteInt32BigEndian(length, checked((int)fileContent.Length));
            hash.AppendData(length);
            hash.AppendData(fileContent.GetBuffer().AsSpan(0, checked((int)fileContent.Length)));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

internal sealed record EmbeddedSkillPackage(string Name, string Version, byte[] Content)
{
    public string Reference => $"{Name}@{Version}";

    public MemoryStream OpenRead() => new(Content, writable: false);
}
