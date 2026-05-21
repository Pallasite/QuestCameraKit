using System;
using UnityEngine;

namespace QuestBuild
{
    /// <summary>
    /// Runtime-readable build identity, baked into the APK by QuestBuilder via a
    /// transient <c>Resources/QuestBuildInfo.json</c> file.
    ///
    /// The build script writes the JSON immediately before <c>BuildPipeline.BuildPlayer</c>
    /// and removes it in a <c>finally</c> block, so the file never lives in git but is
    /// included in every built APK. At runtime the SessionLogger calls <see cref="Load"/>
    /// once and stamps every session sidecar with this identity, so a session can always
    /// be traced back to the exact build that produced it.
    /// </summary>
    [Serializable]
    public class BuildInfo
    {
        public string gitSha = "";
        public string gitBranch = "";
        public bool dirty = false;
        public string bundleVersion = "";
        public string packageName = "";
        public string apkBaseName = "";
        public string buildTimestampUtc = "";
        public string unityVersion = "";
        public int maxSessionsRetainedOnDevice = 50;

        const string ResourcePath = "QuestBuildInfo";

        public static BuildInfo Load()
        {
            try
            {
                var ta = Resources.Load<TextAsset>(ResourcePath);
                if (ta != null && !string.IsNullOrEmpty(ta.text))
                {
                    var info = JsonUtility.FromJson<BuildInfo>(ta.text);
                    if (info != null) return info;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuestBuild] BuildInfo load failed: {e.Message}");
            }
            // Editor play mode or ad-hoc build with no QuestBuildInfo — return blanks
            // so the SessionLogger still runs (sessions will simply be unmatched).
            return new BuildInfo();
        }
    }
}
