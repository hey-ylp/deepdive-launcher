using DeepDive.Core.Content;

namespace DeepDive.Core.Modrinth;

/// <summary>Modrinth 的响应对象 → 通用内容模型。</summary>
/// <remarks>
/// 只做字段搬运，不做补全：<c>Follows</c>、<c>Color</c> 这些 CurseForge 没有的字段
/// 在这里原样保留（有就是有），不要为了「统一」把它们抹掉 —— 界面上关注数对
/// Modrinth 的项目是有效信息。
/// </remarks>
public static class ModrinthMapping
{
    /// <summary>搜索命中 → 通用项目。</summary>
    public static ContentProject ToProject(this ModrinthSearchHit hit) => new()
    {
        Id = hit.ProjectId,
        Slug = hit.Slug,
        Title = hit.Title,
        Description = hit.Description,
        Author = hit.Author,
        IconUrl = hit.IconUrl,
        Downloads = hit.Downloads,
        Follows = hit.Follows,
        DateModified = hit.DateModified,
        Color = hit.Color,
        Categories = hit.Categories,
        Versions = hit.Versions,
        Platform = ContentPlatform.Modrinth,
    };

    /// <summary>版本 → 通用版本。取「主文件」，没有标主文件就取第一个。</summary>
    public static ContentVersionEntry ToEntry(this ModrinthVersion version)
    {
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();

        return new ContentVersionEntry
        {
            Id = version.Id,
            ProjectId = version.ProjectId,
            Name = version.Name,
            VersionNumber = version.VersionNumber,
            ReleaseType = version.VersionType,
            GameVersions = version.GameVersions,
            Loaders = version.Loaders,
            DatePublished = version.DatePublished,
            Downloads = version.Downloads,
            FileName = file?.FileName ?? string.Empty,
            DownloadUrl = file?.Url ?? string.Empty,
            Size = file?.Size ?? 0,
            Sha1 = file?.Hashes.Sha1,
            Dependencies = version.Dependencies
                .Select(d => new ContentDependency
                {
                    ProjectId = d.ProjectId,
                    VersionId = d.VersionId,
                    Type = d.DependencyType,
                })
                .ToList(),
        };
    }
}
