using NUnit.Framework;
using McpUnity.Resources;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace McpUnity.Tests
{
    /// <summary>
    /// Regression tests for GameObject serialization safety.
    /// </summary>
    public class GetGameObjectResourceTests
    {
        private GameObject _testObject;

        [SetUp]
        public void SetUp()
        {
            _testObject = new GameObject("GetGameObjectResourceTests_Object");
        }

        [TearDown]
        public void TearDown()
        {
            if (_testObject != null)
            {
                Object.DestroyImmediate(_testObject);
                _testObject = null;
            }
        }

        [Test]
        public void GameObjectToJObject_WithCollider_SkipsDetailedPropertiesForSafety()
        {
            // Arrange
            _testObject.AddComponent<BoxCollider>();

            // Act
            JObject result = GetGameObjectResource.GameObjectToJObject(_testObject, true);

            // Assert
            Assert.IsNotNull(result);

            JArray components = (JArray)result["components"];
            Assert.IsNotNull(components);

            JObject colliderJson = null;
            foreach (JToken component in components)
            {
                if (component?["type"]?.ToString() == nameof(BoxCollider))
                {
                    colliderJson = (JObject)component;
                    break;
                }
            }

            Assert.IsNotNull(colliderJson, "Expected serialized component list to include BoxCollider.");
            Assert.AreEqual(true, colliderJson["enabled"]?.ToObject<bool>());
            Assert.AreEqual(
                "Detailed property serialization skipped for safety",
                colliderJson["properties"]?["_skipped"]?.ToString());
        }

        [Test]
        public void GameObjectToJObject_WithMaxDepthZero_HasNoChildrenAndIsTruncated()
        {
            // Arrange: parent with one child.
            var child = new GameObject("Child");
            child.transform.SetParent(_testObject.transform);

            try
            {
                // Act
                JObject result = GetGameObjectResource.GameObjectToJObject(
                    _testObject, includeDetailedComponents: true, maxDepth: 0);

                // Assert
                Assert.IsNotNull(result);
                JArray children = (JArray)result["children"];
                Assert.IsNotNull(children);
                Assert.AreEqual(0, children.Count, "maxDepth=0 should not serialize any children.");
                Assert.AreEqual(true, result["_truncated"]?.ToObject<bool>());
                Assert.AreEqual("depth_limit", result["_truncatedReason"]?.ToString());
                Assert.AreEqual(1, result["_childCount"]?.ToObject<int>());
            }
            finally
            {
                Object.DestroyImmediate(child);
            }
        }

        [Test]
        public void GameObjectToJObject_WithMaxDepthOne_StopsAtGrandchildren()
        {
            // Arrange: parent → child → grandchild.
            var child = new GameObject("Child");
            child.transform.SetParent(_testObject.transform);
            var grandchild = new GameObject("Grandchild");
            grandchild.transform.SetParent(child.transform);

            try
            {
                // Act
                JObject result = GetGameObjectResource.GameObjectToJObject(
                    _testObject, includeDetailedComponents: true, maxDepth: 1);

                // Assert: parent has child, but child must be flagged truncated and have no grandchildren.
                Assert.IsNotNull(result);
                Assert.IsNull(result["_truncated"], "Root should not be truncated when its own children fit at maxDepth=1.");

                JArray children = (JArray)result["children"];
                Assert.AreEqual(1, children.Count);

                JObject childJson = (JObject)children[0];
                Assert.AreEqual("Child", childJson["name"]?.ToString());

                JArray grandchildren = (JArray)childJson["children"];
                Assert.AreEqual(0, grandchildren.Count, "Grandchildren should be omitted at depth=1.");
                Assert.AreEqual(true, childJson["_truncated"]?.ToObject<bool>());
                Assert.AreEqual("depth_limit", childJson["_truncatedReason"]?.ToString());
                Assert.AreEqual(1, childJson["_childCount"]?.ToObject<int>());
            }
            finally
            {
                Object.DestroyImmediate(grandchild);
                Object.DestroyImmediate(child);
            }
        }

        [Test]
        public void GameObjectToJObject_WithIncludeComponentsFalse_OmitsComponents()
        {
            // Arrange
            _testObject.AddComponent<Rigidbody>();

            // Act
            JObject result = GetGameObjectResource.GameObjectToJObject(
                _testObject, includeDetailedComponents: true, includeComponents: false);

            // Assert
            Assert.IsNotNull(result);
            Assert.IsFalse(result.ContainsKey("components"),
                "Expected 'components' key to be omitted when includeComponents=false.");
        }

        [Test]
        public void GameObjectToJObject_WithIncludeComponentPropertiesFalse_OmitsProperties()
        {
            // Arrange
            _testObject.AddComponent<Rigidbody>();

            // Act
            JObject result = GetGameObjectResource.GameObjectToJObject(
                _testObject, includeDetailedComponents: true, includeComponentProperties: false);

            // Assert
            JArray components = (JArray)result["components"];
            Assert.IsNotNull(components);

            JObject rigidbodyJson = null;
            foreach (JToken component in components)
            {
                if (component?["type"]?.ToString() == nameof(Rigidbody))
                {
                    rigidbodyJson = (JObject)component;
                    break;
                }
            }

            Assert.IsNotNull(rigidbodyJson, "Expected Rigidbody to be listed.");
            Assert.IsNotNull(rigidbodyJson["type"]);
            Assert.IsNotNull(rigidbodyJson["enabled"]);
            Assert.IsFalse(rigidbodyJson.ContainsKey("properties"),
                "Expected 'properties' to be omitted on each component when includeComponentProperties=false.");
        }
    }
}
