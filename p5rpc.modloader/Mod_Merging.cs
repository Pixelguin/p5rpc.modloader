using System.Diagnostics;
using CriFs.V2.Hook.Interfaces;
using CriFs.V2.Hook.Interfaces.Structs;
using Persona.Merger.Cache;
using Persona.Merger.Patching.Tbl;

namespace p5rpc.modloader;

public partial class Mod
{
    private object _binderInputLock = new();
    
    private void OnBind(ICriFsRedirectorApi.BindContext context)
    {
        if (Game == Game.P4G)
            return;
        
        // Wait for cache to init first.
        _createMergedFileCacheTask.Wait();
        
        // Table merging
        // Note: Actual merging logic is optimised but code in mod could use some more work.
        var watch = Stopwatch.StartNew();
        var cpks = _criFsApi.GetCpkFilesInGameDir();
        var pathToFileMap = context.RelativePathToFileMap;
        var tasks = new List<Task>();
        tasks.Add(Task.Run(() => PatchTbl(pathToFileMap, @"R2\BATTLE\TABLE\SKILL.TBL", TblType.Skill, cpks)));
        tasks.Add(Task.Run(() => PatchTbl(pathToFileMap, @"R2\BATTLE\TABLE\ELSAI.TBL", TblType.Elsai, cpks)));
        tasks.Add(Task.Run(() => PatchTbl(pathToFileMap, @"R2\BATTLE\TABLE\ITEM.TBL", TblType.Item, cpks)));
        tasks.Add(Task.Run(() => PatchTbl(pathToFileMap, @"R2\BATTLE\TABLE\EXIST.TBL", TblType.Exist, cpks)));
        tasks.Add(Task.Run(() => PatchTbl(pathToFileMap, @"R2\BATTLE\TABLE\PLAYER.TBL", TblType.Player, cpks)));
        tasks.Add(Task.Run(() => PatchTbl(pathToFileMap, @"R2\BATTLE\TABLE\ENCOUNT.TBL", TblType.Encount, cpks)));
        tasks.Add(Task.Run(() => PatchTbl(pathToFileMap, @"R2\BATTLE\TABLE\PERSONA.TBL", TblType.Persona, cpks)));
        tasks.Add(Task.Run(() => PatchTbl(pathToFileMap, @"R2\BATTLE\TABLE\AICALC.TBL", TblType.AiCalc, cpks)));
        tasks.Add(Task.Run(() => PatchTbl(pathToFileMap, @"R2\BATTLE\TABLE\VISUAL.TBL", TblType.Visual, cpks)));
        tasks.Add(Task.Run(() => PatchTbl(pathToFileMap, @"R2\BATTLE\TABLE\UNIT.TBL", TblType.Unit, cpks)));

        // TODO: Name
        Task.WhenAll(tasks).Wait();
        _logger.Info("Merging Completed in {0}ms", watch.ElapsedMilliseconds);
        _mergedFileCache.RemoveExpiredItems();
        _ = _mergedFileCache.ToPathAsync();
    }

    private async Task PatchTbl(Dictionary<string, List<ICriFsRedirectorApi.BindFileInfo>> pathToFileMap, string tblPath, TblType type, string[] cpks)
    {
        if (!pathToFileMap.TryGetValue(tblPath, out var candidates)) 
            return;

        var pathInCpk = RemoveR2Prefix(tblPath);
        if (!TryFindFileInAnyCpk(pathInCpk, cpks, out var cpkPath, out var cpkEntry, out int fileIndex))
        {
            _logger.Warning("Unable to find TBL in any CPK {0}", pathInCpk);
            return;
        }
        
        // Build cache key
        var cacheKey = GetCacheKeyAndSources(tblPath, candidates, out var sources);
        if (_mergedFileCache.TryGet(cacheKey, sources, out var cachedFilePath))
        {
            _logger.Info("Loading Merged TBL {0} from Cache ({1})", tblPath, cachedFilePath);
            ReplaceFileInBinderInput(pathToFileMap, pathInCpk, cachedFilePath);
            return;
        }
        
        // Else Merge our Data
        // First we extract.
        _logger.Info("Merging {0} with key {1}.", tblPath, cacheKey);
        await using var cpkStream = new FileStream(cpkPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var reader         = _criFsApi.GetCriFsLib().CreateCpkReader(cpkStream, false);
        using var extractedTable = reader.ExtractFile(cpkEntry.Files[fileIndex].File);
        
        // Then we merge
        var patcher = new TblPatcher(extractedTable.Span.ToArray(), type);
        var patches = new List<TblPatch>(candidates.Count);
        for (var x = 0; x < candidates.Count; x++)
            patches.Add(patcher.GeneratePatch(await File.ReadAllBytesAsync(candidates[x].FullPath)));

        var patched = patcher.Apply(patches);
        
        // Then we store in cache.
        var item = await _mergedFileCache.AddAsync(cacheKey, sources, patched);
        ReplaceFileInBinderInput(pathToFileMap, pathInCpk, Path.Combine(_mergedFileCache.CacheFolder, item.RelativePath));
        _logger.Info("Merge {0} Complete. Cached to {1}.", tblPath, item.RelativePath);
    }

    private void ReplaceFileInBinderInput(Dictionary<string, List<ICriFsRedirectorApi.BindFileInfo>> binderInput, string filePath, string newFilePath)
    {
        lock (_binderInputLock)
        {
            binderInput[filePath] = new List<ICriFsRedirectorApi.BindFileInfo>()
            {
                new()
                {
                    FullPath = newFilePath,
                    ModId = "p5rpc.modloader",
                    LastWriteTime = DateTime.UtcNow
                }
            };
        }
    }

    private static string GetCacheKeyAndSources(string filePath, List<ICriFsRedirectorApi.BindFileInfo> files, out CachedFileSource[] sources)
    {
        var modIds = new string[files.Count];
        sources = new CachedFileSource[files.Count];
        
        for (var x = 0; x < files.Count; x++)
        {
            modIds[x] = files[x].ModId;
            sources[x] = new CachedFileSource()
            {
                LastWrite = files[x].LastWriteTime
            };
        }

        return MergedFileCache.CreateKey(filePath, modIds);
    }

    private bool TryFindFileInAnyCpk(string filePath, string[] cpkFiles, out string cpkPath, out CpkCacheEntry cachedFile, out int fileIndex)
    {
        foreach (var cpk in cpkFiles)
        {
            cpkPath = cpk;
            cachedFile = _criFsApi.GetCpkFilesCached(cpk);

            if (cachedFile.FilesByPath.TryGetValue(filePath, out fileIndex))
                return true;
        }

        cpkPath = string.Empty;
        fileIndex = -1;
        cachedFile = default;
        return false;
    }

    private static string RemoveR2Prefix(string input)
    {
        return input.StartsWith(@"R2\") 
            ? input.Substring(@"R2\".Length) 
            : input;
    }
}