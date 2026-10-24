using JetBrains.Annotations;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Networking.NexusWebApi;

/// <summary>
/// A mod page as a requirement list on Nexus Mods names it.
/// </summary>
[PublicAPI]
public sealed record ModReference(ModUid Uid, string Name);

/// <summary>
/// One page of a mod's "mods requiring this file" list.
/// </summary>
/// <param name="Uid">The mod the list belongs to.</param>
/// <param name="TotalCount">How many mods the whole list holds, across every page.</param>
/// <param name="Dependents">The mods on this page, each of which lists <paramref name="Uid"/> as a requirement.</param>
[PublicAPI]
public sealed record ModDependentsPage(ModUid Uid, int TotalCount, ModReference[] Dependents);

/// <summary>
/// One file on a mod page.
/// </summary>
/// <param name="FileId">The file.</param>
/// <param name="IsMain">Whether the page files it under its main files.</param>
/// <param name="IsCurrent">False for an old version, or a file archived or removed from the page.</param>
/// <param name="IsPrimary">Whether the page marks it as the file to download.</param>
/// <param name="UploadedAt">When it was uploaded.</param>
[PublicAPI]
public sealed record ModFileSummary(FileId FileId, bool IsMain, bool IsCurrent, bool IsPrimary, DateTimeOffset UploadedAt);

/// <summary>
/// The category a mod page is filed under.
/// </summary>
/// <param name="Uid">The mod.</param>
/// <param name="CategoryName">The category, or null when it has none.</param>
[PublicAPI]
public sealed record ModCategoryAssignment(ModUid Uid, string? CategoryName);

/// <summary>
/// What a mod page says about itself and what it needs.
/// </summary>
/// <param name="Uid">The mod.</param>
/// <param name="Name">Its name on the site.</param>
/// <param name="Status">Its status on the site, such as <c>published</c> or <c>removed</c>.</param>
/// <param name="CategoryName">The category it is filed under, when it has one.</param>
/// <param name="NexusRequirements">The mods on Nexus Mods it requires. Requirements hosted elsewhere are left out.</param>
[PublicAPI]
public sealed record ModRequirementDetails(
    ModUid Uid,
    string Name,
    string Status,
    string? CategoryName,
    ModReference[] NexusRequirements);
