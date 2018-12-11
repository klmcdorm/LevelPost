﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Drawing.Imaging;
using YamlDotNet.RepresentationModel;
using System.Globalization;

namespace LevelPost
{
    class ConvertStats
    {
        public int totalTextures;
        public int convertedTextures;
        public int missingTextures;
        public int builtInTextures;
        public int alreadyTextures;
        public int convertedEntities;
    }

    class ConvertSettings
    {
        public bool verbose;
        public List<string> texDirs;
        public List<string> ignoreTexDirs;
        public string bundleDir;
        public string bundleName;
        public string bundlePrefix;
        public int texPointPx;
    }

    interface ILevelMod
    {
        bool Init(string levelFilename, ConvertSettings settings, Action<string> log, ConvertStats stats, List<object[]> cmds);
        bool HandleCommand(object[] cmd, List<object[]> ncmds);
        bool IsChanged();
        void Finish(List<object[]> ncmds);
    }

    class TexMod : ILevelMod
    {
        private ConvertSettings settings;
        private Action<string> log;
        private ConvertStats stats;
        private Regex ignore = new Regex(@"^((alien|cc|ec|emissive|ice|ind|lava|mat|matcen|om|rockwall|solid|foundry|lavafall|lightblocks|metalbeam|security|stripewarning|titan|tn|utility|warningsign)_|transparent1$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private Dictionary<Guid, string> assetNames = new Dictionary<Guid, string>();

        public bool Init(string levelFilename, ConvertSettings settings, Action<string> log, ConvertStats stats, List<object[]> cmds)
        {
            this.settings = settings;
            this.log = log;
            this.stats = stats;
            return true;
        }

        private string FindTextureFile(string texName)
        {
            string texBase = texName + ".png";
            var dirs = ignore.IsMatch(texName) ? settings.texDirs : settings.texDirs.Concat(settings.ignoreTexDirs);
            foreach (var dir in dirs)
            {
                var fn = dir + Path.DirectorySeparatorChar + texBase;
                if (File.Exists(fn))
                    return fn;
            }
            if (ignore.IsMatch(texName))
            {
                stats.builtInTextures++;
                if (settings.verbose)
                    log("Ignored texture " + texName);
            }
            else
            {
                stats.missingTextures++;
                log("Missing file " + texBase);
            }
            return null;
        }

        // Return bitmap pixels in ABGR format, top->bottom order
        private static byte[] GetBitmapData(Bitmap bmp, out bool hasAlpha)
        {
            var bData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            // bData.Stride is positive for bottom->top source data
            int stride = -bData.Stride, width = bData.Width, height = bData.Height;
            var srcData = new byte[Math.Abs(stride) * height];
            var dstData = new byte[width * height * 4];
            System.Runtime.InteropServices.Marshal.Copy(bData.Scan0, srcData, 0, srcData.Length);
            bmp.UnlockBits(bData);
            int srcIdx = stride < 0 ? -stride * (height - 1) : 0, dstIdx = 0;
            bool hasAlphaInt = false;
            for (int h = 0; h < height; h++)
            {
                for (int w = 0; w < width; w++)
                {
                    dstData[dstIdx + 0] = srcData[srcIdx + 2];
                    dstData[dstIdx + 1] = srcData[srcIdx + 1];
                    dstData[dstIdx + 2] = srcData[srcIdx + 0];
                    var a = dstData[dstIdx + 3] = srcData[srcIdx + 3];
                    if (a != 255 && !hasAlphaInt)
                        hasAlphaInt = true;
                    srcIdx += 4;
                    dstIdx += 4;
                }
                srcIdx -= width * 4 - stride;
            }
            hasAlpha = hasAlphaInt;
            return dstData;
        }

        public bool HandleCommand(object[] cmd, List<object[]> newCmds)
        {
            if ((VT) cmd[0] == VT.CmdLoadAssetFromAssetBundle)
                assetNames.Add((Guid) cmd[3], (string) cmd[1]);
            if ((VT) cmd[0] == VT.CmdAssetRegisterMaterial)
            {
                stats.totalTextures++;
                var matGuid = (Guid)cmd[1];
                string texName = (string)cmd[3];
                if (texName.StartsWith("$INTERNAL$:") || texName.Equals("$"))
                {
                    if (texName.Equals("$"))
                    {
                        // cannot find original texture name for now
                    }
                    else if (assetNames.TryGetValue(new Guid(texName.Substring("$INTERNAL$:".Length)), out string assetName))
                    {
                        log("Already converted " + assetName);
                    }
                    else
                    {
                        log("Already converted unknown " + texName);
                    }
                    stats.alreadyTextures++;
                    return false;
                }

                /*
                // Load material from asset bundle
                if (bundle == Guid.Empty) {
                    bundle = Guid.NewGuid();
                    ncmds.Add(new object[]{VT.CmdLoadAssetBundle, levelName, "lpmaterials", bundle});
                }
                Guid asset = Guid.NewGuid();
                ncmds.Add(new object[] { VT.CmdLoadAssetFromAssetBundle, cmd[3] + ".mat", bundle, matGuid});
                */

                string texFilename = FindTextureFile(texName);
                if (texFilename == null)
                    return false;

                Bitmap bmp;
                try {
                    bmp = new Bitmap(texFilename);
                } catch (Exception ex) {
                    log("Error loading file " + texFilename + ": " + ex.Message);
                    return false;
                }

                bool hasAlpha;
                var texData = GetBitmapData(bmp, out hasAlpha);
                bool blocky = bmp.Width <= settings.texPointPx;

                var texGuid = Guid.NewGuid();
                newCmds.Add(new object[] { VT.CmdCreateTexture2D, texGuid, bmp.Width, bmp.Height,
                    hasAlpha ? "ARGB32" : "RGB24",
                    false,
                    blocky ? "Point" : "Bilinear",
                    texName, texData });

                /*
                // Load shader from asset bundle if not yet loaded
                if (bundle == Guid.Empty) {
                    bundle = Guid.NewGuid();
                    ncmds.Add(new object[]{VT.CmdLoadAssetBundle, levelName, "lpshaders", bundle});
                }
                if (shaderGuid == Guid.Empty) {
                    shaderGuid = Guid.NewGuid();
                    ncmds.Add(new object[] { VT.CmdFindPrefabReference, "LPStandardShader", shaderGuid });
                }
                // Create new material with loaded shader
                var matGuid = Guid.NewGuid();
                var color = new object[] { VT.Color, 1.0f, 1.0f, 1.0f, 1.0f };
                var texOfs = new object[] { VT.Vector2, 0.0f, 0.0f };
                var texScale = new object[] { VT.Vector2, 1.0f, 1.0f };
                var kws = new object[] { VT.StringArray };
                ncmds.Add(new object[] { VT.CmdCreateMaterial, matGuid, shaderGuid, color, false, texGuid, texOfs, texScale, 0, kws, texName });
                */

                // Create new material with CmdAssetRegisterMaterial for invalid name "$",
                // which creates default green material with simple Diffuse shader,
                // and change material properties to our texture. Diffuse shader only has _MainTex :(
                newCmds.Add(new object[] { VT.CmdAssetRegisterMaterial, matGuid, cmd[2], "$" });
                newCmds.Add(new object[] { VT.CmdMaterialSetTexture, matGuid, "_MainTex", texGuid });
                var color = new object[] { VT.Color, 1.0f, 1.0f, 1.0f, 1.0f };
                newCmds.Add(new object[] { VT.CmdMaterialSetColor, matGuid, "_Color", color });

                stats.convertedTextures++;
                stats.totalTextures++;
                var msgOpts = new List<string>();
                if (blocky)
                    msgOpts.Add("blocky");
                //if (hasAlpha)
                //    msgOpts.Add("alpha");
                log("Converted texture " + texName + (msgOpts.Count != 0 ? " (" + String.Join(", ", msgOpts) + ")" : ""));
                return true;
            }
            return false;
        }
        public bool IsChanged()
        {
            return stats.convertedTextures != 0;
        }
        public void Finish(List<object[]> ncmds)
        {
        }
    }

    class BunRef
    {
        private Guid bundle;
        private ConvertSettings settings;
        public Action<string> log;

        public void Init(ConvertSettings settings)
        {
            this.settings = settings;
        }

        public Guid GetGuid(List<object[]> newCmds) {
            // Load material from asset bundle
            if (bundle == Guid.Empty) {
                bundle = Guid.NewGuid();
                log("Using bundle " + settings.bundleDir + "\\windows\\" + settings.bundleName);
                newCmds.Add(new object[]{VT.CmdLoadAssetBundle, settings.bundleDir, settings.bundleName, bundle });
            }
            return bundle;
        }
    }

    class BunTexMod : ILevelMod
    {
        private ConvertSettings settings;
        private Action<string> log;
        private ConvertStats stats;
        public BunRef bunRef;

        public bool Init(string levelFilename, ConvertSettings settings, Action<string> log, ConvertStats stats, List<object[]> cmds)
        {
            this.settings = settings;
            this.log = log;
            this.stats = stats;
            return true;
        }

        public bool HandleCommand(object[] cmd, List<object[]> newCmds)
        {
            if ((VT)cmd[0] == VT.CmdLoadAssetFromAssetBundle && ((string)cmd[1]).EndsWith(".mat"))
                stats.alreadyTextures++;
            if ((VT)cmd[0] == VT.CmdAssetRegisterMaterial)
            {
                //stats.totalTextures++;
                var matGuid = (Guid)cmd[1];
                string texName = (string)cmd[3];

                if (texName.StartsWith(settings.bundlePrefix))
                {
                    newCmds.Add(new object[] { VT.CmdLoadAssetFromAssetBundle, cmd[3] + ".mat", bunRef.GetGuid(newCmds), matGuid});

                    stats.convertedTextures++;
                    log("Converted bundle texture " + texName);
                    return true;
                }
            }
            return false;
        }
        public bool IsChanged()
        {
            return stats.convertedTextures != 0;
        }
        public void Finish(List<object[]> ncmds)
        {
        }
    }

    class EntityReplaceMod : ILevelMod
    {
        private ConvertSettings settings;
        private Action<string> log;
        private ConvertStats stats;
        private Guid bundle;
        public BunRef bunRef;
        private readonly Dictionary<Guid, int> objIdx = new Dictionary<Guid, int>();
        private readonly Dictionary<Guid, string> prefabNames = new Dictionary<Guid, string>();
        private readonly Dictionary<string, Guid> newPrefabIds = new Dictionary<string, Guid>();
        private readonly Dictionary<string, string> prefabConvNames = new Dictionary<string, string>()
            { { "entity_PROP_N0000_MINE", "entity_mine" } };

        public bool Init(string levelFilename, ConvertSettings settings, Action<string> log, ConvertStats stats, List<object[]> cmds)
        {
            this.settings = settings;
            this.log = log;
            this.stats = stats;

            var compObj = new Dictionary<Guid, Guid>();
            foreach (var cmd in cmds)
                if ((VT)cmd[0] == VT.CmdGetComponentAtRuntime)
                    compObj.Add((Guid)cmd[4], (Guid)cmd[3]);
                else if ((VT)cmd[0] == VT.CmdGameObjectSetComponentProperty &&
                    (string)cmd[2] == "m_index" &&
                    cmd[5] is int &&
                    compObj.TryGetValue((Guid)cmd[1], out Guid obj))
                    objIdx.Add(obj, (int)cmd[5]);
            return true;
        }

        public bool HandleCommand(object[] cmd, List<object[]> newCmds)
        {
            if ((VT)cmd[0] == VT.CmdFindPrefabReference)
            {
                var prefabName = (string)cmd[1];
                var prefabId = (Guid)cmd[2];
                if (prefabConvNames.ContainsKey(prefabName))
                {
                    prefabNames.Add(prefabId, prefabName);
                    return true;
                }
            } else if ((VT)cmd[0] == VT.CmdInstantiatePrefab) {
                var prefabId = (Guid)cmd[1];
                var objId = (Guid)cmd[2];
                if (prefabNames.TryGetValue(prefabId, out string prefabName))
                {
                    int idx = 0;
                    objIdx.TryGetValue(objId, out idx);
                    string newPrefabName = prefabConvNames[prefabName] + "_" + idx;
                    if (!newPrefabIds.TryGetValue(newPrefabName, out Guid newPrefabId))
                    {
                        newPrefabId = Guid.NewGuid();
                        newPrefabIds.Add(newPrefabName, newPrefabId);
                        newCmds.Add(new object[] { VT.CmdLoadAssetFromAssetBundle, newPrefabName, bunRef.GetGuid(newCmds), newPrefabId });
                    }
                    newCmds.Add(new object[] { cmd[0], newPrefabId, cmd[2], cmd[3] }); // instantiate new prefab
                    log("Converted bundle entity " + prefabName + " to " + newPrefabName);
                    stats.convertedEntities++;
                    return true;
                }
            }
            return false;
        }
        public bool IsChanged()
        {
            return stats.convertedEntities != 0;
        }
        public void Finish(List<object[]> ncmds)
        {
        }
    }


#if TWEAKS
    class EntityTweaker : ILevelMod
    {
        private Action<string> log;

        private List<Tuple<string, Guid>> prefabInsts = new List<Tuple<string, Guid>>();
        private Dictionary<Tuple<string, Guid>, Guid> comps = new Dictionary<Tuple<string, Guid>, Guid>();
        private Dictionary<string, YamlMappingNode> yamlEnts = null;
        private Dictionary<Guid, string> assetNames = new Dictionary<Guid, string>();
        private bool changed = false;

        public bool Init(string levelFilename, ConvertSettings settings, Action<string> log, ConvertStats stats, List<object[]> cmds)
        {
            this.log = log;
            var lpFilename = new Regex(@"[.][a-z]{1,5}$", RegexOptions.IgnoreCase).Replace(levelFilename, "_levelpost.txt");
            if (File.Exists(lpFilename))
            {
                var yaml = new YamlStream();
                try
                {
                    using (var stream = File.OpenText(lpFilename))
                    {
                        yaml.Load(stream);
                        var map = (YamlMappingNode)yaml.Documents[0].RootNode;
                        if (map.Children.TryGetValue(new YamlScalarNode("entities"), out YamlNode ents))
                        {
                            yamlEnts = new Dictionary<string, YamlMappingNode>();
                            foreach (var c in ((YamlMappingNode)ents).Children)
                                yamlEnts.Add(c.Key.ToString().ToLowerInvariant(), (YamlMappingNode)c.Value);
                        }
                    }
                    log("Loaded " + lpFilename);
                }
                catch (Exception ex)
                {
                    log("Error loading " + lpFilename + ": " + ex.Message);
                    return false;
                }
            }
            return true;
        }

        public bool HandleCommand(object[] cmd, List<object[]> newCmds)
        {
            if ((VT)cmd[0] == VT.CmdFindPrefabReference)
                assetNames.Add((Guid)cmd[2], (string)cmd[1]);
            if ((VT)cmd[0] == VT.CmdInstantiatePrefab && yamlEnts != null && assetNames.TryGetValue((Guid)cmd[1], out string name))
            {
                prefabInsts.Add(new Tuple<string,Guid>(name, (Guid)cmd[2]));
            }
            if ((VT)cmd[0] == VT.CmdGetComponentAtRuntime)
            {
                comps.Add(new Tuple<string, Guid>((string)cmd[2], (Guid)cmd[3]), (Guid)cmd[4]);
            }
            return false;
        }
    
        public bool IsChanged()
        {
            return changed;
        }

        public void Finish(List<object[]> ncmds)
        {
            foreach (var x in prefabInsts)
            {
                string name = x.Item1;
                Guid go = (Guid)x.Item2;
                if (yamlEnts.TryGetValue(name.ToLowerInvariant(), out YamlMappingNode ent))
                {
                    foreach (var compMap in ent.Children)
                    {
                        string compName = compMap.Key.ToString();
                        Guid comp;
                        if (!comps.TryGetValue(new Tuple<string, Guid>(compName, go), out comp))
                        {
                            comp = Guid.NewGuid();
                            ncmds.Add(new object[] { VT.CmdGetComponentAtRuntime, false, compName, go, comp });
                        }
                        foreach (var prop in ((YamlMappingNode)compMap.Value).Children)
                        {
                            var sval = prop.Value.ToString();
                            float fval;
                            float.TryParse(sval, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out fval);
                            string propName = prop.Key.ToString();
                            object val =
                                propName == "m_fire_projectile" ? (object)(new object[] { VT.Enum, (int)fval, "Overload.ProjPrefab" }) :
                                propName == "robot_type" ? (object)(new object[] { VT.Enum, (int)fval, "Overload.EnemyType" }) :
                                propName == "hideFlags" ? (object)(new object[] { VT.Enum, (int)fval, "UnityEngine.HideFlags" }) :
                                propName == "m_firing_distribution" ? (object)(new object[] { VT.Enum, (int)fval, "Overload.FiringDistribution" }) :
                                propName.StartsWith("AI_robot_") || propName.StartsWith("AI_legal_") ? sval.Equals("true", StringComparison.InvariantCultureIgnoreCase) ? true : false :
                                propName.StartsWith("m_burst_fire_") ? (int)fval :
                                propName == "m_bonus_drop1" || propName == "m_bonus_drop2" ?
                                    (object)(new object[] { VT.Enum, (int)fval, "Overload.ItemPrefab" }) :
                                fval;
                            ncmds.Add(new object[] { VT.CmdGameObjectSetComponentProperty, comp, propName, (byte)0, (byte)0, val });
                            log("Set " + name + " " + compMap.Key + " " + prop.Key + " to " + val);
                            changed = true;
                        }
                    }
                }
            }
        }
    }
#endif

    class LevelConvert
    {
        public static ConvertStats Convert(string levelFilename, ConvertSettings settings, Action<string> log)
        {
            var level = LevelFile.ReadLevel(levelFilename);

            var stats = new ConvertStats();

            var mods = new List<ILevelMod>();

            if (settings.bundlePrefix != null) {
                var bufRef = new BunRef() { log = log };
                bufRef.Init(settings);
                mods.Add(new BunTexMod() { bunRef = bufRef });
                mods.Add(new EntityReplaceMod() { bunRef = bufRef });
            }

            mods.Add(new TexMod());
            #if TWEAKS
            mods.Add(new EntityTweaker());
            #endif

            foreach (var mod in mods)
                if (!mod.Init(levelFilename, settings, log, stats, level.cmds))
                    return stats;

            var newCmds = new List<object[]>();

            foreach (var cmd in level.cmds)
            {
                if ((VT)cmd[0] != VT.CmdDone &&
                    !mods.Any(mod => mod.HandleCommand(cmd, newCmds)))
                    newCmds.Add(cmd);
            }

            foreach (var mod in mods)
                mod.Finish(newCmds);

            newCmds.Add(new object[] { VT.CmdDone });

            if (mods.Any(mod => mod.IsChanged()))
                LevelFile.WriteLevel(levelFilename, new Level() { version = level.version, cmds = newCmds });
            return stats;
        }
    }
}
