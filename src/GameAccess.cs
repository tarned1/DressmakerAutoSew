using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DressmakerAutoSew
{
    static class GameAccess
    {
        static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();
        static readonly Dictionary<string, MemberInfo> Members = new Dictionary<string, MemberInfo>();
        static readonly Dictionary<string, MemberInfo> Statics = new Dictionary<string, MemberInfo>();

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

        public static object Find(string typeName)
        {
            Type type = TypeOf(typeName);
            if (type == null)
                return null;
            return UnityEngine.Object.FindFirstObjectByType(type);
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

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

        public static float Float(object target, string name, float fallback)
        {
            object value = Member(target, name);
            return value is float number ? number : fallback;
        }

        public static bool Bool(object target, string name)
        {
            return Member(target, name) is bool flag && flag;
        }

        public static bool SceneIs(string sceneName)
        {
            object scene = Member(Instance("GameManager"), "CurrentScene");
            return scene != null && scene.ToString() == sceneName;
        }
    }
}
