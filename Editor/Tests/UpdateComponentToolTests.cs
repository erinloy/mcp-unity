using System;
using McpUnity.Tools;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace McpUnity.Tests
{
    public class UpdateComponentToolTests
    {
        private const string TestAssetDir = "Assets/UpdateComponentToolTests";
        private const string TestAssetPath = TestAssetDir + "/ReferencedAsset.asset";

        private GameObject _gameObject;
        private ScriptableObject _referencedAsset;

        [SetUp]
        public void SetUp()
        {
            _gameObject = new GameObject("UpdateComponentToolTestObject");

            if (!AssetDatabase.IsValidFolder(TestAssetDir))
            {
                AssetDatabase.CreateFolder("Assets", "UpdateComponentToolTests");
            }

            _referencedAsset = ScriptableObject.CreateInstance<ScriptableObject>();
            AssetDatabase.CreateAsset(_referencedAsset, TestAssetPath);
            AssetDatabase.SaveAssets();
        }

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_gameObject);
            }

            if (AssetDatabase.IsValidFolder(TestAssetDir))
            {
                AssetDatabase.DeleteAsset(TestAssetDir);
            }

            AssetDatabase.Refresh();
        }

        [Test]
        public void Execute_WithPrivateSerializedFieldInBaseClass_UpdatesValue()
        {
            var component = _gameObject.AddComponent<DerivedUpdateComponentToolTestComponent>();
            var tool = new UpdateComponentTool();

            JObject result = tool.Execute(new JObject
            {
                ["instanceId"] = UnityObjectId.GetObjectId(_gameObject),
                ["componentName"] = nameof(DerivedUpdateComponentToolTestComponent),
                ["componentData"] = new JObject
                {
                    ["_baseValue"] = 42
                }
            });

            Assert.IsTrue(result["success"]?.Value<bool>() ?? false, result.ToString());
            Assert.AreEqual(42, component.BaseValue);
        }

        [Test]
        public void Execute_WithNestedObjectReferencePath_UpdatesReference()
        {
            var component = _gameObject.AddComponent<UpdateComponentToolTestComponent>();
            var tool = new UpdateComponentTool();

            JObject result = tool.Execute(new JObject
            {
                ["instanceId"] = UnityObjectId.GetObjectId(_gameObject),
                ["componentName"] = nameof(UpdateComponentToolTestComponent),
                ["componentData"] = new JObject
                {
                    ["_eventReference._event"] = TestAssetPath
                }
            });

            Assert.IsTrue(result["success"]?.Value<bool>() ?? false, result.ToString());
            Assert.AreSame(_referencedAsset, component.EventReference);
        }

        [Test]
        public void Execute_WithMissingObjectReferencePath_ReturnsUpdateError()
        {
            var component = _gameObject.AddComponent<UpdateComponentToolTestComponent>();
            component.SetEventReference(_referencedAsset);
            var tool = new UpdateComponentTool();

            JObject result = tool.Execute(new JObject
            {
                ["instanceId"] = UnityObjectId.GetObjectId(_gameObject),
                ["componentName"] = nameof(UpdateComponentToolTestComponent),
                ["componentData"] = new JObject
                {
                    ["_eventReference._event"] = "Assets/UpdateComponentToolTests/Missing.asset"
                }
            });

            Assert.IsNotNull(result["error"], result.ToString());
            StringAssert.Contains("Could not find asset", result["error"]["message"]?.ToString());
            Assert.AreSame(_referencedAsset, component.EventReference);
        }

    }

    [Serializable]
    public class NestedEventReference
    {
        [SerializeField] private ScriptableObject _event;

        public ScriptableObject Event => _event;

        public void SetEvent(ScriptableObject value)
        {
            _event = value;
        }
    }

    public class UpdateComponentToolTestComponent : MonoBehaviour
    {
        [SerializeField] private NestedEventReference _eventReference = new NestedEventReference();

        public ScriptableObject EventReference => _eventReference.Event;

        public void SetEventReference(ScriptableObject value)
        {
            _eventReference.SetEvent(value);
        }
    }

    public class BaseUpdateComponentToolTestComponent : MonoBehaviour
    {
        [SerializeField] private int _baseValue;

        public int BaseValue => _baseValue;
    }

    public class DerivedUpdateComponentToolTestComponent : BaseUpdateComponentToolTestComponent
    {
    }
}
