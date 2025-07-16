using System;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.Scripting.ScriptCompilation;
using UnityEngine;
using System.Collections.Generic;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

#if UNITY_2021_1_OR_NEWER
using UnityEditor.PackageManager.UI.Internal;
#else
using UnityEditor.PackageManager.UI;
#endif

namespace Coffee.UpmGitExtension
{
    /// <summary>
    /// Обёртка для UpmPackageVersion, предоставляющая расширенные свойства и совместимость с разными версиями Unity.
    /// </summary>
    [Serializable]
    internal class UpmPackageVersionWrapper
    {
        private static readonly Regex regex = new Regex("^(\\d+)\\.(\\d+)\\.(\\d+)(.*)$", RegexOptions.Compiled);
        private static SemVersion? unityVersion;

        private readonly UpmPackageVersion _version;

#if PACKAGE_INFO_HAS_BEEN_REMOVED
        [SerializeField]
        private PackageInfo m_PackageInfo;
#endif

        [SerializeField]
        private string m_MinimumUnityVersion;

        public string fullVersionString { get; private set; }
        public SemVersion semVersion { get; private set; }
        public bool isValid { get; private set; }

        public UpmPackageVersionWrapper(UpmPackageVersion version)
        {
            _version = version ?? throw new ArgumentNullException(nameof(version));

#if PACKAGE_INFO_HAS_BEEN_REMOVED
            if (version is IReflectionPackageInfoProvider provider)
            {
                m_PackageInfo = provider.GetPackageInfo();
            }
#endif

            m_MinimumUnityVersion = UnityVersionToSemver(Application.unityVersion).ToString();

            Deserialize();
        }

        /// <summary>
        /// Минимально поддерживаемая версия Unity.
        /// </summary>
        public string MinimumUnityVersion => m_MinimumUnityVersion;

        /// <summary>
        /// Является ли версия предварительной (pre-release).
        /// </summary>
        public bool IsPreRelease()
        {
            return semVersion.Major == 0 || !string.IsNullOrEmpty(semVersion.Prerelease);
        }

        /// <summary>
        /// Получение PackageInfo (если доступно).
        /// </summary>
        public PackageInfo PackageInfo
        {
#if PACKAGE_INFO_HAS_BEEN_REMOVED
            get => m_PackageInfo;
#else
            get => _version.GetPackageInfo();
#endif
        }

        /// <summary>
        /// Доступ к оригинальной версии.
        /// </summary>
        public UpmPackageVersion Version => _version;

        /// <summary>
        /// Выполняет десериализацию и вычисление свойств.
        /// </summary>
        private void Deserialize()
        {
            semVersion = _version.version ?? new SemVersion();

            var revision = PackageInfo?.git?.revision ?? "";
            if (!revision.Contains(_version.versionString) && !string.IsNullOrEmpty(revision))
            {
                fullVersionString = $"{_version.version} ({revision})";
            }
            else
            {
                fullVersionString = _version.version.ToString();
            }

            try
            {
                if (!unityVersion.HasValue)
                {
                    unityVersion = UnityVersionToSemver(Application.unityVersion);
                }

                var supportedUnityVersion = UnityVersionToSemver(m_MinimumUnityVersion);
                isValid = supportedUnityVersion <= unityVersion.Value;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                isValid = false;
            }
        }

        /// <summary>
        /// Преобразует строку версии Unity в SemVersion.
        /// </summary>
        private static SemVersion UnityVersionToSemver(string version)
        {
            return SemVersionParser.Parse(regex.Replace(version, "$1.$2.$3+$4"));
        }
    }

#if PACKAGE_INFO_HAS_BEEN_REMOVED
    /// <summary>
    /// Интерфейс для получения PackageInfo через reflection или сериализацию.
    /// </summary>
    internal interface IReflectionPackageInfoProvider
    {
        PackageInfo GetPackageInfo();
    }
#endif
}
