#region Using directives
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FTOptix.Core;
using UAManagedCore;
#endregion

/// <summary>NetLogic JsonFile（ResourceUri）变量 → 磁盘路径；加载 JSON 请用 <see cref="PhaseUILayoutModel"/>。</summary>
public static class PhaseUILayoutJson
{
    public static PhaseUILayoutRoot TryLoadFromFile(string path) => PhaseUILayoutModel.TryLoadFromFile(path);

    public static string ResolveLayoutPath(string fileName = "phase_ui_layout.sample.json") =>
        PhaseUILayoutModel.ResolveLayoutPath(fileName);

    /// <summary>NetLogic 上 DataType 为 ResourceUri 的 JsonFile：值为 string 或 CLR ResourceURI，统一解析为可读文件路径。</summary>
    public static string ResolveJsonFilePathFromVariable(IUAVariable jsonFileVar)
    {
        if (jsonFileVar?.Value == null) return null;
        try
        {
            object boxed = jsonFileVar.Value.Value;
            if (boxed == null) return null;

            // 与 UsersManagement 一致：用 Optix 自带的 ResourceUri 解析为 File API 可用的路径（运行时由平台展开 %PROJECTDIR% 等）
            string fromApi = TryResolvePathViaFTOptixResourceUri(boxed);
            if (!string.IsNullOrEmpty(fromApi))
                return fromApi;

            if (boxed is string str)
                return ResolveResourceUriString(str);

            if (boxed is LocalizedText lt && !string.IsNullOrEmpty(lt.Text))
                return ResolveResourceUriString(lt.Text);

            if (IsResourceUriClrObject(boxed))
            {
                string p = ResolveFromResourceUriObject(boxed);
                if (!string.IsNullOrEmpty(p)) return p;
            }

            string text = Convert.ToString(boxed);
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (text.StartsWith("ns=", StringComparison.OrdinalIgnoreCase)
                || text.IndexOf("%PROJECTDIR%", StringComparison.OrdinalIgnoreCase) >= 0)
                return ResolveResourceUriString(text);

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>等价于 <c>new ResourceUri(text).Uri</c>；解析成功且文件存在时返回绝对路径。</summary>
    private static string TryResolvePathViaFTOptixResourceUri(object boxed)
    {
        string text = ResourceUriTextFromBoxedValue(boxed);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var resourceUri = new ResourceUri(text);
            string path = resourceUri.Uri;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static string ResourceUriTextFromBoxedValue(object boxed)
    {
        if (boxed == null) return null;
        if (boxed is string s) return s;
        if (boxed is LocalizedText lt) return lt.Text;
        return Convert.ToString(boxed);
    }

    private static string ResolveResourceUriString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = StripOptixNamespacePrefix(raw.Trim());
        s = s.Replace('/', Path.DirectorySeparatorChar);

        if (File.Exists(s))
            return Path.GetFullPath(s);

        const string marker = "%PROJECTDIR%";
        int m = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        string rel = m >= 0
            ? TrimDirSeparators(s.Substring(m + marker.Length))
            : TrimDirSeparators(s);

        if (!string.IsNullOrEmpty(rel) && File.Exists(rel))
            return Path.GetFullPath(rel);

        // PROJECTDIR / 项目根 / ProjectFiles 候选均在 TryCombineUnderProjectFilesRoots 内（含 EnumerateProjectRootCandidates）
        return TryCombineUnderProjectFilesRoots(rel);
    }

    private static string StripOptixNamespacePrefix(string s)
    {
        if (!s.StartsWith("ns=", StringComparison.OrdinalIgnoreCase)) return s;
        for (int i = 3; i < s.Length; i++)
        {
            if (s[i] == ';' || s[i] == ',')
                return s.Substring(i + 1).Trim();
        }
        return s;
    }

    private static string TrimDirSeparators(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return path.Trim('\\', '/');
    }

    /// <summary>Optix 中 %PROJECTDIR% 多为项目根；资源常写在 %PROJECTDIR%/NetSolution/...，磁盘实为项目根/ProjectFiles/NetSolution/...。</summary>
    private static bool TryCombineProjectRootWithRelative(string projectRoot, string relativePath, out string fullPath)
    {
        fullPath = null;
        if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(relativePath)) return false;
        try
        {
            string root = Path.GetFullPath(projectRoot.TrimEnd('\\', '/'));
            string cand = Path.Combine(root, relativePath);
            if (File.Exists(cand))
            {
                fullPath = Path.GetFullPath(cand);
                return true;
            }
            cand = Path.Combine(root, "ProjectFiles", relativePath);
            if (File.Exists(cand))
            {
                fullPath = Path.GetFullPath(cand);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool IsResourceUriClrObject(object boxed)
    {
        if (boxed == null) return false;
        string name = boxed.GetType().Name ?? "";
        string full = boxed.GetType().FullName ?? "";
        return string.Equals(name, "ResourceURI", StringComparison.Ordinal)
            || full.IndexOf("ResourceURI", StringComparison.Ordinal) >= 0
            || full.IndexOf("ResourceUri", StringComparison.Ordinal) >= 0;
    }

    private static string ResolveFromResourceUriObject(object ru)
    {
        try
        {
            foreach (string member in new[] { "ProjectRelativePath", "URI", "AbsoluteFilePath" })
            {
                string raw = GetMemberString(ru, member);
                if (string.IsNullOrEmpty(raw)) continue;
                if (raw.IndexOf("%PROJECTDIR%", StringComparison.OrdinalIgnoreCase) >= 0
                    || raw.StartsWith("ns=", StringComparison.OrdinalIgnoreCase))
                {
                    string expanded = ResolveResourceUriString(raw);
                    if (!string.IsNullOrEmpty(expanded)) return expanded;
                }
            }

            string abs = GetMemberString(ru, "AbsoluteFilePath");
            if (!string.IsNullOrEmpty(abs) && File.Exists(abs))
                return Path.GetFullPath(abs);

            string uri = GetMemberString(ru, "URI");
            if (!string.IsNullOrEmpty(uri) && File.Exists(uri))
                return Path.GetFullPath(uri);

            string uriType = GetMemberString(ru, "URIType");
            if (!string.IsNullOrEmpty(uriType))
            {
                if (string.Equals(uriType, "ProjectRelative", StringComparison.OrdinalIgnoreCase))
                    return TryCombineUnderProjectFilesRoots(GetMemberString(ru, "ProjectRelativePath"));
                if (string.Equals(uriType, "ApplicationRelative", StringComparison.OrdinalIgnoreCase))
                    return TryUnderApplicationFiles(GetMemberString(ru, "ApplicationRelativePath"));
                if (string.Equals(uriType, "URI", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(uri))
                    return ResolveResourceUriString(uri);
            }

            string pr = GetMemberString(ru, "ProjectRelativePath");
            if (!string.IsNullOrEmpty(pr))
            {
                string r = TryCombineUnderProjectFilesRoots(pr);
                if (!string.IsNullOrEmpty(r)) return r;
            }

            return ResolveResourceUriString(Convert.ToString(ru));
        }
        catch
        {
            return null;
        }
    }

    private static string GetMemberString(object target, string memberName)
    {
        if (target == null || string.IsNullOrEmpty(memberName)) return null;
        try
        {
            object v = null;
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (string.Equals(p.Name, memberName, StringComparison.OrdinalIgnoreCase))
                    {
                        v = p.GetValue(target);
                        goto haveValue;
                    }
                }
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (string.Equals(f.Name, memberName, StringComparison.OrdinalIgnoreCase))
                    {
                        v = f.GetValue(target);
                        goto haveValue;
                    }
                }
            }
        haveValue:
            if (v == null) return null;
            if (v is string s) return s;
            return v.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string TryCombineUnderProjectFilesRoots(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        string rel = TrimDirSeparators(relativePath.Replace('/', Path.DirectorySeparatorChar));
        string relUnderPf = StripLeadingProjectFilesSegment(rel);

        foreach (string projectRoot in EnumerateProjectRootCandidates())
        {
            if (TryCombineProjectRootWithRelative(projectRoot, rel, out string fromRoot))
                return fromRoot;
        }

        foreach (string pfRoot in EnumerateProjectFilesDirectories())
        {
            string cand = Path.Combine(pfRoot, relUnderPf);
            if (File.Exists(cand)) return Path.GetFullPath(cand);
        }

        return PhaseUILayoutModel.ResolveLayoutPath(Path.GetFileName(rel));
    }

    /// <summary>去掉开头的 ProjectFiles\，便于在已是 ProjectFiles 目录的基路径下拼接。</summary>
    private static string StripLeadingProjectFilesSegment(string rel)
    {
        if (string.IsNullOrEmpty(rel)) return rel;
        const string prefix = "ProjectFiles";
        if (!rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return rel;
        if (rel.Length == prefix.Length) return string.Empty;
        if (rel[prefix.Length] == Path.DirectorySeparatorChar || rel[prefix.Length] == '/')
            return rel.Substring(prefix.Length + 1).TrimStart(Path.DirectorySeparatorChar);
        return rel;
    }

    private static IEnumerable<string> EnumerateProjectRootCandidates()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void addDir(string p)
        {
            if (string.IsNullOrEmpty(p)) return;
            try
            {
                string full = Path.GetFullPath(p.Trim());
                if (Directory.Exists(full)) set.Add(full);
            }
            catch { }
        }

        try
        {
            string env = Environment.GetEnvironmentVariable("PROJECTDIR");
            if (!string.IsNullOrEmpty(env)) addDir(env);

            foreach (string pf in EnumerateProjectFilesDirectories())
            {
                try
                {
                    var di = new DirectoryInfo(pf);
                    if (di.Parent != null) addDir(di.Parent.FullName);
                }
                catch { }
            }
        }
        catch { }

        return set;
    }

    private static IEnumerable<string> EnumerateProjectFilesDirectories()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void addDir(string p)
        {
            if (string.IsNullOrEmpty(p)) return;
            try
            {
                string full = Path.GetFullPath(p.Trim());
                if (Directory.Exists(full)) set.Add(full);
            }
            catch { }
        }

        try
        {
            string envPf = Environment.GetEnvironmentVariable("OPTIX_PROJECTFILES");
            if (!string.IsNullOrEmpty(envPf)) addDir(envPf);

            string asmDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var dirInfo = string.IsNullOrEmpty(asmDir) ? null : new DirectoryInfo(asmDir);
            for (int i = 0; i < 14 && dirInfo != null; i++, dirInfo = dirInfo.Parent)
                addDir(Path.Combine(dirInfo.FullName, "ProjectFiles"));
        }
        catch { }

        return set;
    }

    private static string TryUnderApplicationFiles(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        string rel = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        try
        {
            string asmDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(asmDir)) return null;
            string cand = Path.Combine(asmDir, rel);
            if (File.Exists(cand)) return Path.GetFullPath(cand);
            for (var dirInfo = new DirectoryInfo(asmDir); dirInfo != null; dirInfo = dirInfo.Parent)
            {
                string walk = Path.Combine(dirInfo.FullName, "ApplicationFiles", rel);
                if (File.Exists(walk)) return Path.GetFullPath(walk);
            }
        }
        catch { }
        return null;
    }
}
