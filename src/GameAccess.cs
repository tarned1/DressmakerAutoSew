using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DressmakerAutoSew
{
    /// <summary>
    /// Reflection helpers: read and write the game's own fields, properties and objects by name.
    /// The game's classes are private to us, so instead of normal C# access (room.maximumFabricLength)
    /// the mod asks "does this object have a member called X? give me its value".
    /// Every lookup is cached in a dictionary, because finding a member by name is slow and
    /// the mod does it thousands of times per second.
    /// If a name doesn't exist (for example after a game update renamed it), the helpers return
    /// null or a fallback instead of crashing.
    /// </summary>
    static class GameAccess
    {
        /// <summary>
        /// Cache: class name -&gt; Type, e.g. "CuttingRoom" -&gt; the CuttingRoom class. Filled by TypeOf.
        /// </summary>
        static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();
        /// <summary>
        /// Cache: "FullTypeName.memberName" -&gt; the field or property found on that type.
        /// Used by Member and SetMember (instance members, i.e. belonging to one object).
        /// </summary>
        static readonly Dictionary<string, MemberInfo> Members = new Dictionary<string, MemberInfo>();
        /// <summary>
        /// Cache: "TypeName.memberName" -&gt; a static field or property (one value for the whole class,
        /// like a singleton's Instance). Used by Instance and Static.
        /// </summary>
        static readonly Dictionary<string, MemberInfo> Statics = new Dictionary<string, MemberInfo>();

        /// <summary>
        /// Finds a game class by its name and remembers it.
        /// Returns null if no loaded class has that name. A missing name is not cached, so it is
        /// searched again on every call (slightly wasteful, but harmless).
        /// </summary>
        static Type TypeOf(string name)
        {
            if (name == null)
                return null;
            if (Types.TryGetValue(name, out Type cached))
                return cached;
            Type type = AccessTools.TypeByName(name);
            if (type != null)
                Types[name] = type;
            return type;
        }

        /// <summary>
        /// Returns the game's single shared object of a class, read from its static "Instance"
        /// property or field (the singleton pattern), e.g. Instance("GameManager").
        /// Returns null if the class or its Instance member doesn't exist.
        /// </summary>
        public static object Instance(string typeName)
        {
            Type type = TypeOf(typeName);
            if (type == null)
                return null;
            string key = typeName + ".Instance";
            if (!Statics.TryGetValue(key, out MemberInfo member))
            {
                member = AccessTools.Property(type, "Instance") ?? (MemberInfo)AccessTools.Field(type, "Instance");
                Statics[key] = member;
            }
            if (member is PropertyInfo property)
                return property.GetValue(null, null);
            if (member is FieldInfo field)
                return field.GetValue(null);
            return null;
        }

        /// <summary>
        /// Reads any static property or field of a game class by name, public or private,
        /// e.g. Static("PlayerProgress", "Current"). Returns null if it can't be found.
        /// </summary>
        public static object Static(string typeName, string memberName)
        {
            Type type = TypeOf(typeName);
            if (type == null || memberName == null)
                return null;
            string key = typeName + "." + memberName;
            if (!Statics.TryGetValue(key, out MemberInfo member))
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                member = type.GetProperty(memberName, flags) ?? (MemberInfo)type.GetField(memberName, flags);
                Statics[key] = member;
            }
            if (member is PropertyInfo property)
                return property.GetValue(null, null);
            if (member is FieldInfo field)
                return field.GetValue(null);
            return null;
        }

        /// <summary>
        /// Finds the first active object in the scene whose component is of this class,
        /// e.g. Find("CuttingRoom"). Uses Unity's FindFirstObjectByType, which searches the
        /// whole scene, so it is relatively slow: avoid calling it every frame for many objects.
        /// </summary>
        public static object Find(string typeName)
        {
            Type type = TypeOf(typeName);
            if (type == null)
                return null;
            return UnityEngine.Object.FindFirstObjectByType(type);
        }

        /// <summary>
        /// Search options for Resolve: instance members, public and private, declared on that exact
        /// class (base classes are walked separately in Resolve).
        /// </summary>
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>
        /// Looks for a field, then a property, with this name on the type and then on each base class
        /// in turn (walking up the inheritance chain). Returns null if no class in the chain has it.
        /// </summary>
        static MemberInfo Resolve(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, Flags);
                if (field != null)
                    return field;
                PropertyInfo property = current.GetProperty(name, Flags);
                if (property != null)
                    return property;
            }
            return null;
        }

        /// <summary>
        /// Reads a field or property of an object by name, e.g. Member(room, "fabricRenderer").
        /// The lookup is cached per type. Returns null if the target is null or has no such member,
        /// so calls can be chained safely: Member(Member(a, "b"), "c").
        /// </summary>
        public static object Member(object target, string name)
        {
            if (target == null || name == null)
                return null;
            Type type = target.GetType();
            string key = type.FullName + "." + name;
            if (!Members.TryGetValue(key, out MemberInfo member))
            {
                member = Resolve(type, name);
                Members[key] = member;
            }
            if (member is FieldInfo field)
                return field.GetValue(target);
            if (member is PropertyInfo property)
                return property.GetValue(target, null);
            return null;
        }

        /// <summary>
        /// Writes a field or property of an object by name. Does nothing if the member is missing or
        /// is a read-only property. The value must have the right type, or reflection throws.
        /// </summary>
        public static void SetMember(object target, string name, object value)
        {
            if (target == null || name == null)
                return;
            Type type = target.GetType();
            string key = type.FullName + "." + name;
            if (!Members.TryGetValue(key, out MemberInfo member))
            {
                member = Resolve(type, name);
                Members[key] = member;
            }
            if (member is FieldInfo field)
                field.SetValue(target, value);
            else if (member is PropertyInfo property && property.CanWrite)
                property.SetValue(target, value, null);
        }

        /// <summary>
        /// Reads a member that should be a float. Returns the fallback if it is missing or not a float.
        /// </summary>
        public static float Float(object target, string name, float fallback)
        {
            object value = Member(target, name);
            return value is float number ? number : fallback;
        }

        /// <summary>
        /// Reads a member that should be a bool. Returns false if it is missing or not a bool.
        /// </summary>
        public static bool Bool(object target, string name)
        {
            return Member(target, name) is bool flag && flag;
        }

        /// <summary>
        /// True when the game's current scene (GameManager.Instance.CurrentScene) has this name,
        /// e.g. SceneIs("CuttingRoom").
        /// </summary>
        public static bool SceneIs(string sceneName)
        {
            object scene = Member(Instance("GameManager"), "CurrentScene");
            return scene != null && scene.ToString() == sceneName;
        }
    }
}
