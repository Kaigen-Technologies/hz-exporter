using System;
using System.Collections.Generic;

namespace HZExporter.ShaderGraph
{
    // .shadergraph (multi-object JSON) → SgGraph. The parser is the *only*
    // thing that touches the raw JSON shape; every downstream stage operates on
    // the typed model in SgGraph.cs.
    static class SgParser
    {
        public static SgGraph Parse(string text)
        {
            var raw = new Dictionary<string, Dictionary<string, object>>();
            int i = 0;
            while (true)
            {
                TinyJson.SkipWs(text, ref i);
                if (i >= text.Length) break;
                if (text[i] != '{')
                    throw new Exception($"expected '{{' at {i}, got '{text[i]}'");
                var obj = (Dictionary<string, object>)TinyJson.ParseValue(text, ref i);
                if (obj.TryGetValue("m_ObjectId", out var idObj) && idObj is string id)
                    raw[id] = obj;
            }

            var g = new SgGraph();
            var graphData = FindGraphData(raw);

            ResolveVariant(g, raw, graphData);
            CollectBlockContext(graphData, "m_VertexContext", g.VertexBlockIds);
            CollectBlockContext(graphData, "m_FragmentContext", g.FragmentBlockIds);
            ParseProperties(g, raw, graphData);
            ParseKeywords(g, raw, graphData);
            ParseSlots(g, raw);
            ParseNodes(g, raw);
            ParseEdges(g, graphData);
            ParseAnnotations(g, raw, graphData);

            return g;
        }

        // GroupData (the colored bounding boxes in the editor) + StickyNoteData
        // (free-form text boxes). Pure editor-side annotation — we keep them so
        // codegen can emit `// === Title ===` section headers and `/* … */`
        // sticky-note blocks for readability against the original graph.
        static void ParseAnnotations(SgGraph g,
                                     Dictionary<string, Dictionary<string, object>> raw,
                                     Dictionary<string, object> graphData)
        {
            if (TryGetIdList(graphData, "m_GroupDatas", out var gids))
            {
                foreach (var gid in gids)
                {
                    if (!raw.TryGetValue(gid, out var gobj)) continue;
                    string title = gobj.TryGetValue("m_Title", out var t) ? t as string : null;
                    if (!string.IsNullOrEmpty(title)) g.GroupTitles[gid] = title;
                }
            }
            if (TryGetIdList(graphData, "m_StickyNoteDatas", out var sids))
            {
                foreach (var sid in sids)
                {
                    if (!raw.TryGetValue(sid, out var sobj)) continue;
                    var note = new SgStickyNote
                    {
                        Title = sobj.TryGetValue("m_Title", out var t) ? t as string : null,
                        Content = sobj.TryGetValue("m_Content", out var c) ? c as string : null,
                    };
                    if (sobj.TryGetValue("m_Group", out var go)
                        && go is Dictionary<string, object> gd
                        && gd.TryGetValue("m_Id", out var ggid)
                        && ggid is string ggidStr
                        && !string.IsNullOrEmpty(ggidStr))
                        note.GroupId = ggidStr;
                    g.StickyNotes.Add(note);
                }
            }
            // Stamp each node with its group id (when present).
            foreach (var node in g.Nodes.Values)
            {
                if (node.Raw == null) continue;
                if (node.Raw.TryGetValue("m_Group", out var go)
                    && go is Dictionary<string, object> gd
                    && gd.TryGetValue("m_Id", out var gid)
                    && gid is string gidStr
                    && !string.IsNullOrEmpty(gidStr)
                    && g.GroupTitles.ContainsKey(gidStr))
                    node.GroupId = gidStr;
            }
        }

        // Shader keywords (boolean toggles + enum variants) referenced from
        // the GraphData's m_Keywords array. Each one drives a `#ifdef` branch
        // in the generated shader.
        static void ParseKeywords(SgGraph g,
                                  Dictionary<string, Dictionary<string, object>> raw,
                                  Dictionary<string, object> graphData)
        {
            if (!TryGetIdList(graphData, "m_Keywords", out var kwIds)) return;
            foreach (var kid in kwIds)
            {
                if (!raw.TryGetValue(kid, out var kobj)) continue;
                int ktype = kobj.TryGetValue("m_KeywordType", out var kt) && kt is double ktd ? (int)ktd : 0;
                int dval  = kobj.TryGetValue("m_Value", out var dv) && dv is double dvd ? (int)dvd : 0;
                var kw = new SgKeyword
                {
                    Id = kid,
                    Name = kobj.TryGetValue("m_Name", out var n) ? n as string : null,
                    ReferenceName = kobj.TryGetValue("m_DefaultReferenceName", out var rn) ? rn as string : null,
                    IsBoolean = ktype == 0,
                    DefaultValue = dval,
                };
                if (kobj.TryGetValue("m_Entries", out var es) && es is List<object> entries)
                {
                    foreach (var e in entries)
                    {
                        if (!(e is Dictionary<string, object> ed)) continue;
                        kw.Entries.Add(new SgKeywordEntry
                        {
                            Id = ed.TryGetValue("id", out var eid) && eid is double eidd ? (int)eidd : 0,
                            DisplayName = ed.TryGetValue("displayName", out var dn) ? dn as string : null,
                            ReferenceName = ed.TryGetValue("referenceName", out var ern) ? ern as string : null,
                        });
                    }
                }
                g.Keywords.Add(kw);
                g.KeywordsById[kid] = kw;
            }
        }

        // Material-exposed properties. The GraphData's m_Properties array
        // dictates declaration order; the property objects themselves live
        // elsewhere in the multi-object stream.
        static void ParseProperties(SgGraph g,
                                    Dictionary<string, Dictionary<string, object>> raw,
                                    Dictionary<string, object> graphData)
        {
            if (!TryGetIdList(graphData, "m_Properties", out var propIds)) return;
            foreach (var pid in propIds)
            {
                if (!raw.TryGetValue(pid, out var pobj)) continue;
                string fq = pobj.TryGetValue("m_Type", out var to) ? to as string : "";
                // The reference name is what materials/runtime use to address the
                // property. When the author sets a custom "Reference" in the Graph
                // Inspector, ShaderGraph stores it in m_OverrideReferenceName and leaves
                // m_DefaultReferenceName as the auto GUID name. Prefer the override.
                string overrideRef = pobj.TryGetValue("m_OverrideReferenceName", out var orn) ? orn as string : null;
                string defaultRef = pobj.TryGetValue("m_DefaultReferenceName", out var rn) ? rn as string : null;
                var prop = new SgProperty
                {
                    Id = pid,
                    Name = pobj.TryGetValue("m_Name", out var n) ? n as string : null,
                    ReferenceName = !string.IsNullOrEmpty(overrideRef) ? overrideRef : defaultRef,
                    Type = MapPropertyType(fq),
                };
                if (pobj.TryGetValue("m_Value", out var v)) prop.DefaultValue = v;
                prop.FieldName = MakeFieldName(prop.ReferenceName, prop.Name);
                if (prop.Type == SgType.Texture2D && !string.IsNullOrEmpty(prop.ReferenceName))
                {
                    var lowered = prop.ReferenceName.ToLowerInvariant();
                    prop.IsNormalMap = lowered.Contains("normal") || lowered.Contains("bump");
                }
                g.Properties.Add(prop);
                g.PropertiesById[pid] = prop;
            }
        }

        static SgType MapPropertyType(string fq)
        {
            if (fq.EndsWith(".ColorShaderProperty"))     return SgType.Vec4;
            if (fq.EndsWith(".Texture2DShaderProperty")) return SgType.Texture2D;
            if (fq.EndsWith(".Vector1ShaderProperty"))   return SgType.Float;
            if (fq.EndsWith(".Vector2ShaderProperty"))   return SgType.Vec2;
            if (fq.EndsWith(".Vector3ShaderProperty"))   return SgType.Vec3;
            if (fq.EndsWith(".Vector4ShaderProperty"))   return SgType.Vec4;
            if (fq.EndsWith(".BooleanShaderProperty"))   return SgType.Bool;
            return SgType.Unknown;
        }

        // Field name in MaterialFS — derived from Unity's reference name
        // (`_Color`) by snake-casing (`color`). Falls back to m_Name when no
        // reference name is available.
        static string MakeFieldName(string refName, string fallback)
        {
            string s = !string.IsNullOrEmpty(refName) ? refName : fallback;
            return string.IsNullOrEmpty(s) ? "_unnamed" : SgNames.ToSnakeCase(s);
        }

        // ----- helpers -------------------------------------------------------

        // GraphData is the unique top-level object that owns the vertex/fragment
        // contexts.
        static Dictionary<string, object> FindGraphData(Dictionary<string, Dictionary<string, object>> raw)
        {
            foreach (var kv in raw)
                if (kv.Value.ContainsKey("m_VertexContext")) return kv.Value;
            throw new Exception("no GraphData object in shadergraph");
        }

        static void ResolveVariant(SgGraph g,
                                   Dictionary<string, Dictionary<string, object>> raw,
                                   Dictionary<string, object> graphData)
        {
            string subTargetId = null;
            if (TryGetIdList(graphData, "m_ActiveTargets", out var targetIds))
            {
                foreach (var tid in targetIds)
                {
                    if (!raw.TryGetValue(tid, out var target)) continue;

                    // Pull AlphaClip / SurfaceType / etc. while we're here.
                    if (target.TryGetValue("m_AlphaClip", out var ac) && ac is bool acb) g.AlphaClip = acb;

                    if (target.TryGetValue("m_ActiveSubTarget", out var subRef)
                        && subRef is Dictionary<string, object> subDict
                        && subDict.TryGetValue("m_Id", out var subIdObj)
                        && subIdObj is string sid)
                    {
                        subTargetId = sid;
                    }
                }
            }
            if (subTargetId == null || !raw.TryGetValue(subTargetId, out var subTarget)) return;
            string subType = subTarget.TryGetValue("m_Type", out var t) ? t as string : "";
            if (subType.Contains("UniversalUnlitSubTarget")) g.Variant = SgVariant.Unlit;
            else if (subType.Contains("UniversalLitSubTarget")) g.Variant = SgVariant.Lit;
        }

        static void ParseSlots(SgGraph g, Dictionary<string, Dictionary<string, object>> raw)
        {
            foreach (var kv in raw)
            {
                string type = kv.Value.TryGetValue("m_Type", out var to) ? to as string : "";
                if (!IsSlotType(type)) continue;

                var s = new SgSlot
                {
                    Id = kv.Key,
                    Type = ShortTypeName(type),
                };
                if (kv.Value.TryGetValue("m_Id", out var nid) && nid is double nidD) s.NumId = (int)nidD;
                if (kv.Value.TryGetValue("m_ShaderOutputName", out var on)) s.OutputName = on as string;
                if (kv.Value.TryGetValue("m_DisplayName", out var dn)) s.DisplayName = dn as string;
                if (kv.Value.TryGetValue("m_SlotType", out var st) && st is double stD) s.IsOutput = (int)stD == 1;

                // m_Value is the user-typed inspector literal; m_DefaultValue is
                // the slot's compile-time fallback (usually identity/zero). We
                // read m_Value — that's where the actual graph data lives.
                if (kv.Value.TryGetValue("m_Value", out var v)) s.Value = v;
                else if (kv.Value.TryGetValue("m_DefaultValue", out var dv)) s.Value = dv;

                g.Slots[s.Id] = s;
            }
        }

        static void ParseNodes(SgGraph g, Dictionary<string, Dictionary<string, object>> raw)
        {
            foreach (var kv in raw)
            {
                string type = kv.Value.TryGetValue("m_Type", out var to) ? to as string : "";
                if (!IsNodeType(type)) continue;

                var node = new SgNode
                {
                    Id = kv.Key,
                    Type = ShortTypeName(type),
                    Raw = kv.Value,
                };
                if (kv.Value.TryGetValue("m_SerializedDescriptor", out var sd))
                    node.SerializedDescriptor = sd as string;

                if (TryGetIdList(kv.Value, "m_Slots", out var sids))
                {
                    foreach (var sid in sids)
                    {
                        if (!g.Slots.TryGetValue(sid, out var slot)) continue;
                        node.SlotByNum[slot.NumId] = sid;
                        if (!string.IsNullOrEmpty(slot.DisplayName))
                            node.SlotByName[slot.DisplayName] = slot.NumId;
                    }
                }
                g.Nodes[node.Id] = node;
            }
        }

        static void ParseEdges(SgGraph g, Dictionary<string, object> graphData)
        {
            if (!graphData.TryGetValue("m_Edges", out var edgesObj)) return;
            if (!(edgesObj is List<object> edgesList)) return;
            foreach (var eo in edgesList)
            {
                if (!(eo is Dictionary<string, object> ed)) continue;
                var e = new SgEdge();
                ParseSlotRef(ed, "m_OutputSlot", out e.OutputNodeId, out e.OutputSlotIdNum);
                ParseSlotRef(ed, "m_InputSlot",  out e.InputNodeId,  out e.InputSlotIdNum);
                g.Edges.Add(e);
            }
        }

        static void ParseSlotRef(Dictionary<string, object> ed, string key, out string nodeId, out int slotNum)
        {
            nodeId = null; slotNum = 0;
            if (!ed.TryGetValue(key, out var v) || !(v is Dictionary<string, object> d)) return;
            if (d.TryGetValue("m_Node", out var nr) && nr is Dictionary<string, object> nd
                && nd.TryGetValue("m_Id", out var nid)) nodeId = nid as string;
            if (d.TryGetValue("m_SlotId", out var sid) && sid is double sd) slotNum = (int)sd;
        }

        static void CollectBlockContext(Dictionary<string, object> graphData, string key, List<string> outIds)
        {
            if (!graphData.TryGetValue(key, out var ctxObj)) return;
            if (!(ctxObj is Dictionary<string, object> ctx)) return;
            if (!ctx.TryGetValue("m_Blocks", out var blocksObj)) return;
            if (!(blocksObj is List<object> blocks)) return;
            foreach (var b in blocks)
                if (b is Dictionary<string, object> bd && bd.TryGetValue("m_Id", out var id) && id is string s)
                    outIds.Add(s);
        }

        static bool TryGetIdList(Dictionary<string, object> obj, string key, out List<string> ids)
        {
            ids = new List<string>();
            if (!obj.TryGetValue(key, out var v) || !(v is List<object> list)) return false;
            foreach (var item in list)
                if (item is Dictionary<string, object> d && d.TryGetValue("m_Id", out var idObj) && idObj is string s)
                    ids.Add(s);
            return true;
        }

        static bool IsSlotType(string fq) =>
            fq.EndsWith("MaterialSlot");

        static bool IsNodeType(string fq)
        {
            if (string.IsNullOrEmpty(fq)) return false;
            string sn = ShortTypeName(fq);
            // Matches XxxNode plus a few peripheral types (RedirectNodeData,
            // SampleGradient — which doesn't end in "Node"). Whitelist
            // explicit non-"Node" suffixes here as Unity adds them.
            if (sn.EndsWith("Node")) return true;
            if (sn == "RedirectNodeData") return true;
            if (sn == "SampleGradient") return true;
            return false;
        }

        static string ShortTypeName(string fq)
        {
            int dot = fq.LastIndexOf('.');
            return dot >= 0 ? fq.Substring(dot + 1) : fq;
        }
    }
}
