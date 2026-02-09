using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditorInternal;
using UnityEngine;

#if UNITY_2021_1_OR_NEWER
using UnityEditor.PackageManager.UI.Internal;
#else
using UnityEditor.PackageManager.UI;
#endif

#if UNITY_2023_1_OR_NEWER
using UpmPackage = UnityEditor.PackageManager.UI.Internal.Package;
#endif

namespace Coffee.UpmGitExtension
{
    public sealed class GitUpmPackageVersion
    {
        public string NAME;
        public string UNIQUE_ID;
        public string VERSION;
        public string GIT_HASH;
        public string GIT_REVISION;

#if UNITY_6000_0_OR_NEWER
	    internal UpmPackageVersion UPM;
#else
        internal UpmPackageVersionEx UPM;
#endif
    }

    [Serializable]
    internal sealed class FetchResultRaw
    {
        public string id;
        public string url;
        public int hash;

#if UNITY_6000_0_OR_NEWER
        public UpmPackageVersion[] versions;
#else
	public UpmPackageVersionEx[] versions;
#endif
    }

    
    [Serializable]
    internal class FetchResult : ISerializationCallbackReceiver
    {
        public string id;
        public string url;
        public int hash;

        public GitUpmPackageVersion[] versions;


        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
#if UNITY_6000_0_OR_NEWER
            versions = versions.ToArray();
#else
            versions = versions
                .Where(v => v.isValid)
                .ToArray();
#endif
        }

        public override int GetHashCode()
        {
            return hash;
        }

        public override bool Equals(object obj)
        {
            return (obj as FetchResult)?.hash == hash;
        }
    }

    public class GitPackageDatabase : ScriptableSingleton<GitPackageDatabase>
    {
        private static string _workingDirectory => InternalEditorUtility.unityPreferencesFolder + "/GitPackageDatabase";
        private static string _serializeVersion => "2.0.2";
        private static string _resultsDir => _workingDirectory + "/Results-" + _serializeVersion;
        private static FileSystemWatcher _watcher;
        private static bool _isPaused;
        private static readonly HashSet<FetchResult> _resultCaches = new HashSet<FetchResult>();

        private static PackageManagerProjectSettings _settings =>
            ScriptableSingleton<PackageManagerProjectSettings>.instance;

#if UNITY_2020_1
        internal static IUpmClient _upmClient => UpmClient.instance;
        internal static IPackageDatabase _packageDatabase => PackageDatabase.instance;
#else
        internal static UpmClient _upmClient => ScriptableSingleton<ServicesContainer>.instance.Resolve<UpmClient>();
        internal static PackageDatabase _packageDatabase =>
            ScriptableSingleton<ServicesContainer>.instance.Resolve<PackageDatabase>();
#endif

        public static void Install(string packageId)
        {
            _upmClient.AddByUrl(packageId);
        }

        public static void Install(string packageId, Action<bool> callback = null)
        {
            _upmClient.AddByUrl(packageId);

            if (callback != null)
            {
                void OnAddOperation(IOperation op)
                {
                    op.onOperationSuccess += OnSuccess;
                    op.onOperationError += OnError;
                }

                void OnSuccess(IOperation op)
                {
                    callback?.Invoke(true);
                    Unsubscribe();
                }

                void OnError(IOperation op, UIError error)
                {
                    callback?.Invoke(false);
                    Unsubscribe();
                }

                void Unsubscribe()
                {
                    if (_upmClient != null)
                    {
                        _upmClient.onAddOperation -= OnAddOperation;
                    }
                }

                _upmClient.onAddOperation += OnAddOperation;
            }
        }

        public static void Install(string name, string hash = "", Action<bool> callback = null)
        {
            UpmPackageVersion package = null;
    
            if (!string.IsNullOrEmpty(hash))
            {
                package = GetPackage(name, hash)?.UPM;
            }

            if (package == null)
            {
                callback?.Invoke(false);
                return;
            }

            Install(package.uniqueId, callback);
        }

        public static void Uninstall(string packageId)
        {
            var i = packageId.IndexOf('@');
            var packageName = packageId.Substring(0, i);
            _upmClient.RemoveByName(packageName);
        }

        internal static IEnumerable<UpmPackage> GetUpmPackages()
        {
            return _packageDatabase.allPackages
                .OfType<UpmPackage>()
                .Where(x => x.versions.primary.HasTag(PackageTag.Git));
        }

        internal static IEnumerable<UpmPackage> GetInstalledGitPackages()
        {
            return GetUpmPackages()
                .Where(p => p.GetInstalledVersion()?.HasTag(PackageTag.Git) == true);
        }

        public static void Fetch(string url, Action<int> callback = null)
        {
            const string kFetchPackagesJs = "Packages/com.coffee.upm-git-extension/Editor/Commands/fetch-packages.js";
            NodeJs.Run(_workingDirectory, Path.GetFullPath(kFetchPackagesJs), url, code =>
            {
                if (code == 0)
                {
                    GitRepositoryUrlList.AddUrl(url);
                }

                callback?.Invoke(code);
            });
        }

        internal static IPackage GetPackage(IPackageVersion packageVersion)
        {
            return _packageDatabase.GetPackage(packageVersion.name);
        }

        internal static IPackage GetPackage(string packageName)
        {
            return _packageDatabase.GetPackage(packageName);
        }

        public static GitUpmPackageVersion GetPackage(string packageName, string hash)
        {
            var result = _resultCaches
                .SelectMany(r => r.versions)
                .FirstOrDefault(v => v.NAME == packageName && v.GIT_HASH == hash);

            return result;
        }

        public static GitUpmPackageVersion GetPackageByVersion(string packageName, string version)
        {
            var result = _resultCaches
                .SelectMany(r => r.versions)
                .FirstOrDefault(v => v.NAME == packageName && v.VERSION == version);

            return result;
        }

#if UNITY_6000_0_OR_NEWER
        internal static List<GitUpmPackageVersion> GetPackageVersion(string packageName, string versionUniqueId)
        {
            var result = _resultCaches
                .SelectMany(r => r.versions)
                .Where(v => v.UPM.name == packageName).ToList();

            return result;
        }
#else
        internal static IPackageVersion GetPackageVersion(string packageUniqueId, string versionUniqueId)
        {
            IPackage package;
            IPackageVersion version;
            _packageDatabase.GetPackageAndVersion(packageUniqueId, versionUniqueId, out package, out version);
            return version;
        }
#endif

        public static void Fetch()
        {
            GetInstalledGitPackages()
                .Select(p => p?.versions?.primary?.GetPackageInfo()?.GetSourceUrl())
                .Where(url => !string.IsNullOrEmpty(url))
                .Distinct()
                .ForEach(url => Fetch(url));
        }

        public static void OpenCacheDirectory()
        {
            if (Directory.Exists(_workingDirectory))
            {
                EditorUtility.RevealInFinder(_workingDirectory);
            }
        }

        public static void ClearCache()
        {
            _resultCaches.Clear();

            if (Directory.Exists(_workingDirectory))
            {
                foreach (var dir in Directory.GetDirectories(_workingDirectory))
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, true);
                    }
                }
            }

            Debug.Log("[GitPackageDatabase] Clear Cache");
            WatchResultJson();
        }

        public static void ResetCacheTime()
        {
            _isPaused = true;
            var resultDir = Path.GetFullPath(_resultsDir);
            if (Directory.Exists(resultDir))
            {
                foreach (var file in Directory.GetFiles(resultDir, "*.json"))
                {
                    File.SetLastWriteTime(file, DateTime.Now.AddMinutes(-10));
                }
            }

            _isPaused = false;
        }

#if UNITY_6000_0_OR_NEWER
        public static IEnumerable<GitUpmPackageVersion> GetAvailablePackageVersions(string repoUrl = null)
        {
            var result = _resultCaches
                .SelectMany(r => r.versions)
                .Where(v => string.IsNullOrEmpty(repoUrl) || v.UPM.uniqueId.Contains(repoUrl));

            return result;
        }
#else
        internal static IEnumerable<UpmPackageVersionEx> GetAvailablePackageVersions(string repoUrl = null)
        {
            return _resultCaches.SelectMany(r => r.versions)
                .Where(v => v.isValid && (string.IsNullOrEmpty(repoUrl) || v.uniqueId.Contains(repoUrl)));
        }
#endif

        public static void RequestUpdateGitPackageVersions()
        {
            EditorApplication.delayCall -= UpdateGitPackageVersions;
            EditorApplication.delayCall += UpdateGitPackageVersions;
        }

        private static void UpdateGitPackageVersions()
        {
            var installedIds = new HashSet<string>(
                GetUpmPackages()
                    .Where(p => p.GetInstalledVersion() != null)
                    .Select(p => p.name)
            );

            var packages = GetAvailablePackageVersions()
                .ToLookup(v => v.UPM.name)
                .Select(versions =>
                {
                    var isInstalled = installedIds.Contains(versions.Key);
                    if (!isInstalled)
                    {
                        return null;
                    }

                    // Git mode: Register all installable package versions.

                    var upmPackage = _packageDatabase.GetPackage(versions.Key) as UpmPackage;
                    var installedVersion = upmPackage?.versions.installed as UpmPackageVersion;
                    if (installedVersion.GetPackageInfo().source != PackageSource.Git)
                    {
                        return upmPackage;
                    }

                    // Unlock.
                    installedVersion.UnlockVersion();

#if UNITY_6000_0_OR_NEWER
                    upmPackage = upmPackage.UpdateVersionsSafety();
#else
                    var newVersions = new[] { new UpmPackageVersionEx(installedVersion) }
                        .Concat(versions.Where(v => v.uniqueId != installedVersion.uniqueId))
                        .OrderBy(v => v.semVersion)
                        .ThenBy(v => v.isInstalled)
                        .ToArray();
                    upmPackage = upmPackage.UpdateVersionsSafety(newVersions);
#endif

                    return upmPackage;
                })
                .Where(p => p != null);

            EditorApplication.delayCall += () => UpdatePackages(packages);

#if UNITY_2021_1_OR_NEWER
            if (!_settings.seeAllPackageVersions)
            {
                _settings.seeAllPackageVersions = true;
                _settings.Save();
            }
#endif
        }

        private static void UpdatePackages(IEnumerable<IPackage> packages)
        {
#if UNITY_2023_1_OR_NEWER
            _packageDatabase.UpdatePackages(packages.ToList());
#else
            _packageDatabase.Call("OnPackagesChanged", packages);
#endif
        }

        private static void OnResultFileCreated(string file)
        {
            if (_isPaused || string.IsNullOrEmpty(file) || Path.GetExtension(file) != ".json" || !File.Exists(file))
            {
                return;
            }

            try
            {
                var text = File.ReadAllText(file, Encoding.UTF8);
                var raw = JsonUtility.FromJson<FetchResultRaw>(text);
                if (raw == null || raw.versions == null)
                {
                    return;
                }

                var gitMap = ParseGitInfo(text);

                var versions = raw.versions
                    .Select(v =>
                    {
                        if (v == null || string.IsNullOrEmpty(v.uniqueId))
                        {
                            return null;
                        }

                        if (!gitMap.TryGetValue(v.uniqueId, out var git))
                        {
                            return null;
                        }

                        return new GitUpmPackageVersion
                        {
                            NAME = v.name,
                            UNIQUE_ID = v.uniqueId,
                            VERSION = v.versionString,
                            GIT_HASH = git.hash,
                            GIT_REVISION = git.revision,
                            UPM = v
                        };
                    })
                    .Where(v => v != null && !string.IsNullOrEmpty(v.GIT_HASH))
                    .ToArray();

                var result = new FetchResult
                {
                    id = raw.id,
                    url = raw.url,
                    hash = raw.hash,
                    versions = versions
                };

                _resultCaches.RemoveWhere(r => r.url == result.url);
                _resultCaches.Add(result);
                RequestUpdateGitPackageVersions();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
        
        private static Dictionary<string, (string hash, string revision)> ParseGitInfo(string json)
        {
            var map = new Dictionary<string, (string, string)>();

            var root = JObject.Parse(json);
            var versions = root["versions"] as JArray;
            if (versions == null)
            {
                return map;
            }

            foreach (var v in versions)
            {
                var pkg = v["m_PackageInfo"];
                if (pkg == null)
                {
                    continue;
                }

                var uniqueId = pkg["m_PackageId"]?.ToString();
                if (string.IsNullOrEmpty(uniqueId))
                {
                    continue;
                }

                var git = pkg["m_Git"];
                if (git == null)
                {
                    continue;
                }

                var hash = git["m_Hash"]?.ToString();
                if (string.IsNullOrEmpty(hash))
                {
                    continue;
                }

                var revision = git["m_Revision"]?.ToString();
                map[uniqueId] = (hash, revision);
            }

            return map;
        }
        
        [InitializeOnLoadMethod]
        private static void WatchResultJson()
        {
            _resultCaches.Clear();

#if !UNITY_EDITOR_WIN
            Environment.SetEnvironmentVariable("MONO_MANAGED_WATCHER", "enabled");
#endif
            var resultDir = Path.GetFullPath(_resultsDir);
            if (!Directory.Exists(resultDir))
            {
                Directory.CreateDirectory(resultDir);
            }

            RequestUpdateGitPackageVersions();
            foreach (var file in Directory.GetFiles(resultDir, "*.json"))
            {
                EditorApplication.delayCall += () => OnResultFileCreated(Path.Combine(resultDir, file));
            }

            _watcher?.Dispose();
            _watcher = new FileSystemWatcher
            {
                Path = resultDir,
                NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            _watcher.Created += (s, e) => EditorApplication.delayCall += () => OnResultFileCreated(e.FullPath);

#if UNITY_6000_3_OR_NEWER
            var addOp = _upmClient.Get("addAndRemoveOperation") as UpmAddAndRemoveOperation;
            if (addOp != null)
            {
                addOp.onOperationFinalized += _ =>
                {
                    RequestUpdateGitPackageVersions();
                };
            }
#else
            _upmClient.onAddOperation += op => op.onOperationFinalized += _ => RequestUpdateGitPackageVersions();
#endif
        }

        internal static string GetShortPackageId(UpmPackageVersion self)
        {
            var semver = self.versionString;
            var revision = ExtractGitRevision(self.uniqueId);

            return !string.IsNullOrEmpty(revision) && !revision.Contains(semver)
                ? $"{self.name}/{revision} ({semver})"
                : $"{self.name}/{semver}";
        }

        internal static string GetShortVersion(UpmPackageVersion self)
        {
            var semver = self.versionString;
            var revision = ExtractGitRevision(self.uniqueId);

            return !string.IsNullOrEmpty(revision) && !revision.Contains(semver)
                ? $"{revision} ({semver})"
                : $"{semver}";
        }

        private static string ExtractGitRevision(string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId))
            {
                return null;
            }

            var hashIndex = uniqueId.LastIndexOf('#');
            if (hashIndex < 0 || hashIndex == uniqueId.Length - 1)
            {
                return null;
            }

            return uniqueId.Substring(hashIndex + 1);
        }
    }
}
