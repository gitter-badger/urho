//
// Runtime C# support
//
// Authors:
//   Miguel de Icaza (miguel@xamarin.com)
//
// Copyrigh 2015 Xamarin INc
//
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Urho.Resources;

namespace Urho
{
	internal class Runtime
	{
		static readonly RefCountedCache RefCountedCache = new RefCountedCache();
		static Dictionary<Type, int> hashDict;
		static MonoRefCountedCallback monoRefCountedCallback; //keep references to native callbacks (protect from GC)
		static MonoComponentCallback monoComponentCallback;

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		delegate void MonoRefCountedCallback(IntPtr ptr, RefCountedEvent rcEvent);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		delegate void MonoComponentCallback(IntPtr componentPtr, IntPtr xmlElementPtr, MonoComponentCallbackType eventType);

		[DllImport(Consts.NativeImport, CallingConvention = CallingConvention.Cdecl)]
		static extern void RegisterMonoRefCountedCallback(MonoRefCountedCallback callback);

		[DllImport(Consts.NativeImport, CallingConvention = CallingConvention.Cdecl)]
		static extern void RegisterMonoComponentCallback(MonoComponentCallback callback);

		/// <summary>
		/// Runtime initialization. 
		/// </summary>
		public static void Initialize()
		{
			RegisterMonoRefCountedCallback(monoRefCountedCallback = OnRefCountedEvent);
			RegisterMonoComponentCallback(monoComponentCallback = OnComponentEvent);
		}

		/// <summary>
		/// This method is called by RefCounted::~RefCounted or RefCounted::AddRef
		/// </summary>
		[MonoPInvokeCallback(typeof(MonoRefCountedCallback))]
		static void OnRefCountedEvent(IntPtr ptr, RefCountedEvent rcEvent)
		{
			if (rcEvent == RefCountedEvent.Delete)
			{
				var referenceHolder = RefCountedCache.Get(ptr);
				if (referenceHolder == null)
					return; //we don't have this object in the cache so let's just skip it

				var reference = referenceHolder.Reference;
				if (reference == null)
				{
					// seems like the reference was Weak and GC has removed it - remove item from the dictionary
					RefCountedCache.Remove(ptr);
				}
				else
				{
					reference.HandleNativeDelete();
				}
			}
			else if (rcEvent == RefCountedEvent.Addref)
			{
				//if we have an object with this handle and it's reference is weak - then change it to strong.
				var referenceHolder = RefCountedCache.Get(ptr);
				referenceHolder?.MakeStrong();
			}
		}

		[MonoPInvokeCallback(typeof(MonoComponentCallback))]
		static void OnComponentEvent(IntPtr componentPtr, IntPtr xmlElementPtr, MonoComponentCallbackType eventType)
		{
			const string typeNameKey = "SharpTypeName";
			var xmlElement = new XmlElement(xmlElementPtr);
			if (eventType == MonoComponentCallbackType.SaveXml)
			{
				var component = LookupObject<Component>(componentPtr, false);
				if (component != null && component.TypeName != component.GetType().Name)
				{
					xmlElement.SetString(typeNameKey, component.GetType().AssemblyQualifiedName);
					component.OnSerialize(new XmlComponentSerializer(xmlElement));
				}
			}
			else if (eventType == MonoComponentCallbackType.LoadXml)
			{
				var name = xmlElement.GetAttribute(typeNameKey);
				if (!string.IsNullOrEmpty(name))
				{
					Component component;
					try
					{
						component = (Component) Activator.CreateInstance(Type.GetType(name), componentPtr);
					}
					catch (Exception exc)
					{
						throw new InvalidOperationException($"{name} doesn't override constructor Component(IntPtr handle).", exc);
					}
					component.OnDeserialize(new XmlComponentSerializer(xmlElement));
					if (component.Node != null)
					{
						component.OnAttachedToNode(component.Node);
					}
				}
			}
			else if (eventType == MonoComponentCallbackType.AttachedToNode)
			{
				var component = LookupObject<Component>(componentPtr, false);
				component?.OnAttachedToNode(component.Node);
			}
		}

		public static T LookupRefCounted<T> (IntPtr ptr, bool createIfNotFound = true) where T:RefCounted
		{
			if (ptr == IntPtr.Zero)
				return null;

			var reference = RefCountedCache.Get(ptr)?.Reference;
			if (reference is T)
				return (T) reference;

			if (!createIfNotFound)
				return null;

			var refCounted = (T)Activator.CreateInstance(typeof(T), ptr);
			return refCounted;
		}

		public static T LookupObject<T>(IntPtr ptr, bool createIfNotFound = true) where T : UrhoObject
		{
			if (ptr == IntPtr.Zero)
				return null;

			var referenceHolder = RefCountedCache.Get(ptr);
			var reference = referenceHolder?.Reference;
			if (reference is T) //possible collisions
				return (T)reference;

			if (!createIfNotFound)
				return null;

			var name = Marshal.PtrToStringAnsi(UrhoObject.UrhoObject_GetTypeName(ptr));
			var type = FindTypeByName(name);
			var typeInfo = type.GetTypeInfo();
			if (typeInfo.IsSubclassOf(typeof(Component)) || type == typeof(Component))
			{
				//TODO: special case, handle managed subclasses
			}

			var urhoObject = (T)Activator.CreateInstance(type, ptr);
			return urhoObject;
		}

		public static void UnregisterObject (IntPtr handle)
		{
			RefCountedCache.Remove(handle);
		}

		public static void RegisterObject (RefCounted refCounted)
		{
			RefCountedCache.Add(refCounted);
		}
		
		public static StringHash LookupStringHash (Type t)
		{
			if (hashDict == null)
				hashDict = new Dictionary<Type, int> ();

			int c;
			if (hashDict.TryGetValue (t, out c))
				return new StringHash (c);
			var hash = GetTypeStatic(t);
			hashDict [t] = hash.Code;
			return hash;
		}

		static StringHash GetTypeStatic(Type type)
		{
			var typeStatic = type.GetRuntimeProperty("TypeStatic");
			while (typeStatic == null)
			{
				type = type.GetTypeInfo().BaseType;
				if (type == typeof(object))
					throw new InvalidOperationException("The type doesn't have static TypeStatic property");
				typeStatic = type.GetRuntimeProperty("TypeStatic");
			}
			return (StringHash)typeStatic.GetValue(null);
		}

		static internal IReadOnlyList<T> CreateVectorSharedPtrProxy<T> (IntPtr handle) where T : UrhoObject
		{
			return new Vectors.ProxyUrhoObject<T> (handle);
		}

		static internal IReadOnlyList<T> CreateVectorSharedPtrRefcountedProxy<T>(IntPtr handle) where T : RefCounted
		{
			return new Vectors.ProxyRefCounted<T>(handle);
		}

		internal static void Cleanup()
		{
			RefCountedCache.Clean();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}

		// for Debug purposes
		static internal int KnownObjectsCount => RefCountedCache.Count;

		static Dictionary<string, Type> typesByNativeNames;
		// special cases: (TODO: share this code with SharpieBinder somehow)
		static Dictionary<string, string> typeNamesMap = new Dictionary<string, string>
			{
				{nameof(UrhoObject),  "Object"},
				{nameof(UrhoConsole), "Console"},
				{nameof(XmlFile),     "XMLFile"},
				{nameof(JsonFile),    "JSONFile"},
			};

		static Type FindTypeByName(string name)
		{
			if (typesByNativeNames == null)
			{
				typesByNativeNames = new Dictionary<string, Type>(200);
				foreach (var type in typeof(Runtime).GetTypeInfo().Assembly.ExportedTypes)
				{
					if (!type.GetTypeInfo().IsSubclassOf(typeof(RefCounted)))
						continue;

					string remappedName;
					if (!typeNamesMap.TryGetValue(type.Name, out remappedName))
						remappedName = type.Name;

					typesByNativeNames[remappedName] = type;
				}
			}
			Type result;
			if (!typesByNativeNames.TryGetValue(name, out result))
				throw new Exception($"Type {name} not found.");

			return result;
		}
	}
}