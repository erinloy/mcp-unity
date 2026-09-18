using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace McpUnity.Utils
{
    public static class UnityObjectId
    {
#if MCP_UNITY_ENTITY_ID_API
        private static int _nextSyntheticId = 1;
        private static readonly Dictionary<EntityId, int> PublicIdsByEntityId = new Dictionary<EntityId, int>();
        private static readonly Dictionary<int, EntityId> EntityIdsByPublicId = new Dictionary<int, EntityId>();
#endif

        public static int GetObjectId(Object unityObject)
        {
            if (unityObject == null)
            {
                return 0;
            }

#if MCP_UNITY_ENTITY_ID_API
            EntityId entityId = unityObject.GetEntityId();
            if (!PublicIdsByEntityId.TryGetValue(entityId, out int publicId))
            {
                publicId = _nextSyntheticId++;
                PublicIdsByEntityId[entityId] = publicId;
                EntityIdsByPublicId[publicId] = entityId;
            }

            return publicId;
#else
            return unityObject.GetInstanceID();
#endif
        }

        public static Object ObjectFromId(int objectId)
        {
#if MCP_UNITY_ENTITY_ID_API
            return EntityIdsByPublicId.TryGetValue(objectId, out EntityId entityId)
                ? EditorUtility.EntityIdToObject(entityId)
                : null;
#else
            return EditorUtility.InstanceIDToObject(objectId);
#endif
        }
    }
}
