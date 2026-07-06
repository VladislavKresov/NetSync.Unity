using NetSync.Serialization;
using UnityEngine;

namespace NetSync.Unity {
    /// <summary>
    /// Unity math types for NetWriter/NetReader. Lives in the adapter on purpose:
    /// the core stays engine-agnostic.
    /// </summary>
    public static class NetWriterUnityExtensions {
        public static NetWriter WriteVector2(this NetWriter writer, Vector2 value) {
            return writer.WriteSingle(value.x).WriteSingle(value.y);
        }

        public static Vector2 ReadVector2(this NetReader reader) {
            return new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }

        public static NetWriter WriteVector3(this NetWriter writer, Vector3 value) {
            return writer.WriteSingle(value.x).WriteSingle(value.y).WriteSingle(value.z);
        }

        public static Vector3 ReadVector3(this NetReader reader) {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        public static NetWriter WriteQuaternion(this NetWriter writer, Quaternion value) {
            return writer.WriteSingle(value.x).WriteSingle(value.y).WriteSingle(value.z).WriteSingle(value.w);
        }

        public static Quaternion ReadQuaternion(this NetReader reader) {
            return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
