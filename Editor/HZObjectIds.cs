using UnityEngine;

namespace HZExporter
{
#if UNITY_6000_3_OR_NEWER
    using HzObjectId = UnityEngine.EntityId;
#else
    using HzObjectId = System.Int32;
#endif

    /// <summary>Per-session identity of a Unity object, used as dictionary/set keys during export.
    /// Unity 6.3 deprecated GetInstanceID() and EntityId's int conversion, so the id must stay an
    /// opaque HzObjectId rather than an int.</summary>
    public static class HZObjectIds
    {
        public static HzObjectId HzId(this Object o)
        {
#if UNITY_6000_3_OR_NEWER
            return o.GetEntityId();
#else
            return o.GetInstanceID();
#endif
        }
    }
}
