﻿using Discord;
using Discord.Webhook;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Thaliak.Database;
using Thaliak.Database.Models;
using XIVLauncher.Common.Game.Patch.PatchList;

namespace Thaliak.Poller.Polling;

public class PatchReconciliationService
{
    private readonly ThaliakContext _db;

    public PatchReconciliationService(ThaliakContext db)
    {
        _db = db;
    }

    public void Reconcile(XivRepository repo, PatchListEntry[] remotePatches)
    {
        // use a consistent timestamp through reconciliation of each repo's patch list
        var now = DateTime.UtcNow;

        // get the list of expansions and their repository mappings
        var expansions = _db.ExpansionRepositoryMappings
            .Include(erp => erp.ExpansionRepository)
            .Include(erp => erp.GameRepository)
            .Where(erp => erp.GameRepositoryId == repo.Id)
            .ToList();

        int GetEffectiveRepositoryId(int repositoryId, string patchUrl)
        {
            var expansionId = XivExpansionRepositoryMapping.GetExpansionId(patchUrl);
            if (expansionId == 0)
            {
                return repositoryId;
            }

            foreach (var erp in expansions)
            {
                if (erp.ExpansionId == expansionId)
                {
                    return erp.ExpansionRepositoryId;
                }
            }

            throw new InvalidDataException($"Unknown expansion ID {expansionId} for repository ID {repositoryId}!");
        }

        // attach the repositories so EF knows we're not inserting new repo records
        _db.Repositories.Attach(repo);
        _db.Repositories.AttachRange(expansions.Select(erp => erp.ExpansionRepository));

        // ensure we iterate through all of the expansion repositories as well
        var repoIds = new[] {repo.Id}.Union(expansions.Select(erp => erp.ExpansionRepositoryId));
        var targetDbPatches = _db.Patches.Where(p => repoIds.Contains(p.RepositoryId));
        var targetDbVersions = _db.Versions.Where(v => repoIds.Contains(v.RepositoryId));

        // prepare the list of patches we currently have
        var localPatches = targetDbPatches.Join(
            targetDbVersions,
            patch => patch.Version,
            version => version,
            (patch, version) => new {patch, version}
        );

        // keep track of newly discovered patches
        var newPatchList = new List<XivPatch>();

        // let's go
        foreach (var remotePatch in remotePatches)
        {
            var effectiveRepoId = GetEffectiveRepositoryId(repo.Id, remotePatch.Url);
            var localPatch = localPatches.FirstOrDefault(p =>
                p.version.VersionString == remotePatch.VersionId && p.version.RepositoryId == effectiveRepoId);
            if (localPatch == null)
            {
                Log.Information("Discovered new patch: {@0}", remotePatch);

                // existing version?
                var version = repo.Versions.FirstOrDefault(v =>
                    v.VersionString == remotePatch.VersionId && v.RepositoryId == effectiveRepoId);
                if (version == null)
                {
                    version = new XivVersion
                    {
                        VersionId = XivVersion.StringToId(remotePatch.VersionId),
                        VersionString = remotePatch.VersionId,
                        RepositoryId = effectiveRepoId
                    };
                }
                else
                {
                    _db.Versions.Attach(version);
                }

                // collect patch data
                var newPatch = new XivPatch
                {
                    Version = version,
                    Repository = repo,
                    RemoteOriginPath = remotePatch.Url,
                    Size = remotePatch.Length,
                    // the launcher is offering us the patch now
                    FirstOffered = now,
                    LastOffered = now,
                    // it's safe to assume if the launcher is offering a patch, it exists
                    FirstSeen = now,
                    LastSeen = now,
                    HashType = remotePatch.Url == remotePatch.HashType ? null : remotePatch.HashType,
                    HashBlockSize = remotePatch.HashBlockSize == 0 ? null : remotePatch.HashBlockSize,
                    Hashes = remotePatch.Hashes
                };

                // commit the patch
                _db.Patches.Add(newPatch);

                // add it to the list for alerting
                newPatchList.Add(newPatch);
            }
            else
            {
                Log.Information("Patch already present: {@0}", remotePatch);

                localPatch.patch.LastSeen = now;
                localPatch.patch.LastOffered = now;
                _db.Patches.Update(localPatch.patch);
            }

            // save to DB after each patch so we have a permanent ID to rely on for versions
            _db.SaveChanges();
        }

        /*
         * ———————————No patches?———————————
         * ⠀⣞⢽⢪⢣⢣⢣⢫⡺⡵⣝⡮⣗⢷⢽⢽⢽⣮⡷⡽⣜⣜⢮⢺⣜⢷⢽⢝⡽⣝
         * ⠸⡸⠜⠕⠕⠁⢁⢇⢏⢽⢺⣪⡳⡝⣎⣏⢯⢞⡿⣟⣷⣳⢯⡷⣽⢽⢯⣳⣫⠇
         * ⠀⠀⢀⢀⢄⢬⢪⡪⡎⣆⡈⠚⠜⠕⠇⠗⠝⢕⢯⢫⣞⣯⣿⣻⡽⣏⢗⣗⠏⠀
         * ⠀⠪⡪⡪⣪⢪⢺⢸⢢⢓⢆⢤⢀⠀⠀⠀⠀⠈⢊⢞⡾⣿⡯⣏⢮⠷⠁⠀⠀
         * ⠀⠀⠀⠈⠊⠆⡃⠕⢕⢇⢇⢇⢇⢇⢏⢎⢎⢆⢄⠀⢑⣽⣿⢝⠲⠉⠀⠀⠀⠀
         * ⠀⠀⠀⠀⠀⡿⠂⠠⠀⡇⢇⠕⢈⣀⠀⠁⠡⠣⡣⡫⣂⣿⠯⢪⠰⠂⠀⠀⠀⠀
         * ⠀⠀⠀⠀⡦⡙⡂⢀⢤⢣⠣⡈⣾⡃⠠⠄⠀⡄⢱⣌⣶⢏⢊⠂⠀⠀⠀⠀⠀⠀
         * ⠀⠀⠀⠀⢝⡲⣜⡮⡏⢎⢌⢂⠙⠢⠐⢀⢘⢵⣽⣿⡿⠁⠁⠀⠀⠀⠀⠀⠀⠀
         * ⠀⠀⠀⠀⠨⣺⡺⡕⡕⡱⡑⡆⡕⡅⡕⡜⡼⢽⡻⠏⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
         * ⠀⠀⠀⠀⣼⣳⣫⣾⣵⣗⡵⡱⡡⢣⢑⢕⢜⢕⡝⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
         * ⠀⠀⠀⣴⣿⣾⣿⣿⣿⡿⡽⡑⢌⠪⡢⡣⣣⡟⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
         * ⠀⠀⠀⡟⡾⣿⢿⢿⢵⣽⣾⣼⣘⢸⢸⣞⡟⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
         * ⠀⠀⠀⠀⠁⠇⠡⠩⡫⢿⣝⡻⡮⣒⢽⠋⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
         * —————————————————————————————
         */
        if (newPatchList.Count < 1)
        {
            return;
        }

        // yeah, patches
        Log.Information("Sending Discord alerts for new patches");
        SendDiscordAlerts(newPatchList);
    }

    private void SendDiscordAlerts(List<XivPatch> newPatchList)
    {
        var discordHooks = _db.DiscordHooks.ToList();

        foreach (var hookEntry in discordHooks)
        {
            Log.Information("Sending Discord alert to webhook: {@hookEntry}", hookEntry);

            try
            {
                var embeds = new List<Embed>();

                foreach (var patch in newPatchList)
                {
                    var fields = new List<EmbedFieldBuilder>();

                    fields.Add(new EmbedFieldBuilder
                    {
                        Name = "Repository",
                        Value = $"{patch.Version.Repository.Name} ({patch.Version.Repository.Slug})"
                    });

                    fields.Add(new EmbedFieldBuilder
                    {
                        Name = "Version",
                        Value = patch.Version.VersionString
                    });

                    fields.Add(new EmbedFieldBuilder
                    {
                        Name = "URL",
                        Value = patch.RemoteOriginPath
                    });

                    fields.Add(new EmbedFieldBuilder
                    {
                        Name = "Size",
                        Value = MakeSizePretty(patch.Size)
                    });

                    embeds.Add(new EmbedBuilder
                    {
                        Color = Color.Green,
                        Title = "New FFXIV patch detected",
                        Timestamp = DateTimeOffset.UtcNow,
                        Fields = fields,
                        Footer = new EmbedFooterBuilder
                        {
                            Text = "thaliak.xiv.dev",
                        }
                    }.Build());
                }

                new DiscordWebhookClient(hookEntry.Url).SendMessageAsync(
                    "",
                    false,
                    embeds,
                    "Thaliak",
                    "https://thaliak.xiv.dev/logo512.png"
                );
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error calling Discord webhook");
            }
        }
    }

    // todo: this is garbage, clean it up later, but I'm sleepy so you get this for now
    private static string MakeSizePretty(long len)
    {
        string[] sizes = {"B", "KB", "MB", "GB", "TB"};
        var order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}