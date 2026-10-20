using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace DressmakerAutoSew
{
    public static class PlaceBoard
    {
        static GameObject _tab;
        static GameObject _page;
        static RectTransform _list;
        static ScrollRect _scroll;
        const int MaxFabrics = 3;

        static readonly List<object> Added = new List<object>();
        static bool _open;
        static bool _seeded;
        static bool _adding;
        static int _selected;
        static int _replace = -1;
        static bool _fabricsOpen;
        static string _stamp = "";
        static MonoBehaviour _room;
        static bool _groupTouched;
        static bool _controlWidth;
        static bool _expandWidth;
        static HorizontalLayoutGroup _rowLayout;
        static bool _tabsReady;
        static MonoBehaviour _sidebar;
        static readonly Dictionary<Type, MethodInfo> InitializeMethods = new Dictionary<Type, MethodInfo>();
        static readonly HashSet<string> OpenGroups = new HashSet<string>();
        static readonly string[] Groups = { "Blouse", "Skirt", "Collar", "Sleeves", "Other" };

        public static void Tick(MonoBehaviour room)
        {
            if (!Plugin.PlaceEnabled || !GameAccess.SceneIs("CuttingRoom"))
            {
                if (_open || _seeded || (_tab != null && _tab.activeSelf))
                {
                    Hide();
                    _seeded = false;
                    Added.Clear();
                    _adding = false;
                    _selected = 0;
                    _replace = -1;
                    _fabricsOpen = false;
                    OpenGroups.Clear();
                    _stamp = "";
                    AutoPlace.ClearVisit();
                }
                _sidebar = null;
                return;
            }

            _room = room;
            if (_sidebar == null)
                _sidebar = GameAccess.Find("SidebarInventory") as MonoBehaviour;
            if (_sidebar == null)
                return;
            if (_page == null || _tab == null)
                Ensure(_sidebar);
            if (_tab != null && !_tab.activeSelf)
                _tab.SetActive(true);
            if (!_tabsReady)
                SeatTabs(_sidebar);
            if (!_seeded)
                Seed(room);
            if (!_open || _list == null)
                return;
            // UI actions invalidate the board by clearing _stamp. Do not rescan
            // inventory or rebuild the page continuously while it is open.
            if (_stamp.Length == 0)
                Rebuild(_sidebar);
        }

        public static void TabChanged()
        {
            _open = false;
            if (_page != null)
                _page.SetActive(false);
            SetTabState(_tab, false);
        }

        public static void TabsChanged()
        {
            bool show = Plugin.PlaceEnabled && GameAccess.SceneIs("CuttingRoom");
            if (_tab != null)
                _tab.SetActive(show);
            if (!show)
            {
                TabChanged();
                RestoreTabs();
            }
        }

        static MethodInfo InitializeMethod(Type type)
        {
            if (type == null)
                return null;
            if (InitializeMethods.TryGetValue(type, out MethodInfo cached))
                return cached;
            MethodInfo found = null;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name == "Initialize" && method.GetParameters().Length == 1)
                {
                    found = method;
                    break;
                }
            }
            InitializeMethods[type] = found;
            return found;
        }

        static bool HasInitialize(Type type)
        {
            return InitializeMethod(type) != null;
        }

        static void Seed(MonoBehaviour room)
        {
            var fabrics = AutoPlace.Fabrics();
            object open = AutoPlace.OpenFabric(room);
            if (open == null && fabrics.Count == 0)
                return;
            _seeded = true;
            _adding = false;
            _replace = -1;
            _selected = 0;
            if (open == null)
                open = fabrics[0];
            Added.Add(open);
            GiveAll(AutoPlace.Patterns(GameAccess.Instance("GameManager")), open);
            _stamp = "";
        }

        static void Ensure(MonoBehaviour sidebar)
        {
            var patternTab = GameAccess.Member(sidebar, "patternTab") as Component;
            var fabricTab = GameAccess.Member(sidebar, "fabricTab") as Component;
            var patternParent = GameAccess.Member(sidebar, "patternParent") as Transform;
            if (patternTab == null || fabricTab == null || patternParent == null)
                return;

            if (_tab == null)
            {
                _tab = UnityEngine.Object.Instantiate(patternTab.gameObject, patternTab.transform.parent);
                _tab.name = "LayoutTab";
                int at = fabricTab.transform.GetSiblingIndex() + 1;
                _tab.transform.SetSiblingIndex(Mathf.Clamp(at, 0, _tab.transform.parent.childCount - 1));
                foreach (Button button in _tab.GetComponentsInChildren<Button>(true))
                {
                    StripPersistent(button.onClick);
                    button.onClick.AddListener(Open);
                }
                DisableLocalize(_tab);
                ShareTab(_tab);
            }

            _tab.SetActive(true);
            SetText(_tab, "Layout");
            if (!_open)
                SetTabState(_tab, false);

            if (_page != null)
                return;

            var scroll = patternParent.GetComponentInParent<ScrollRect>();
            Transform host = scroll != null && scroll.viewport != null ? scroll.viewport : patternParent.parent;
            _page = new GameObject("LayoutPage", typeof(RectTransform), typeof(Image));
            _page.transform.SetParent(host, false);
            var pageRect = _page.GetComponent<RectTransform>();
            pageRect.anchorMin = Vector2.zero;
            pageRect.anchorMax = Vector2.one;
            pageRect.offsetMin = Vector2.zero;
            pageRect.offsetMax = Vector2.zero;
            pageRect.pivot = new Vector2(0.5f, 0.5f);
            var backdrop = _page.GetComponent<Image>();
            backdrop.color = new Color(0.16f, 0.11f, 0.09f, 1f);
            backdrop.raycastTarget = true;

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            viewport.transform.SetParent(_page.transform, false);
            var viewRect = viewport.GetComponent<RectTransform>();
            viewRect.anchorMin = Vector2.zero;
            viewRect.anchorMax = Vector2.one;
            viewRect.offsetMin = new Vector2(6f, 8f);
            viewRect.offsetMax = new Vector2(-6f, -8f);
            viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);

            var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);
            _list = content.GetComponent<RectTransform>();
            _list.anchorMin = new Vector2(0f, 1f);
            _list.anchorMax = new Vector2(1f, 1f);
            _list.pivot = new Vector2(0.5f, 1f);
            _list.offsetMin = Vector2.zero;
            _list.offsetMax = Vector2.zero;
            var layout = content.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.padding = new RectOffset(0, 0, 4, 8);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = content.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll = _page.AddComponent<ScrollRect>();
            _scroll.viewport = viewRect;
            _scroll.content = _list;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 24f;
            _page.SetActive(false);
        }

        static void Open()
        {
            var sidebar = GameAccess.Find("SidebarInventory") as MonoBehaviour;
            if (sidebar == null || _page == null)
                return;
            SetActive(GameAccess.Member(sidebar, "fabricParent"), false);
            SetActive(GameAccess.Member(sidebar, "patternParent"), false);
            SetActive(GameAccess.Member(sidebar, "accessoryParent"), false);
            SetActive(GameAccess.Member(sidebar, "filterButton"), false);
            SetActive(GameAccess.Member(sidebar, "blankIndicator"), false);
            _page.SetActive(true);
            _page.transform.SetAsLastSibling();
            _open = true;
            SetTabState(GameAccess.Member(sidebar, "patternTab"), false);
            SetTabState(GameAccess.Member(sidebar, "fabricTab"), false);
            SetTabState(GameAccess.Member(sidebar, "accessoryTab"), false);
            SetTabState(_tab, true);
            _stamp = "";
            Rebuild(sidebar);
        }

        static void SeatTabs(MonoBehaviour sidebar)
        {
            var pattern = GameAccess.Member(sidebar, "patternTab") as Component;
            var fabric = GameAccess.Member(sidebar, "fabricTab") as Component;
            if (pattern == null || fabric == null || _tab == null)
                return;

            var patternRt = pattern.transform as RectTransform;
            var fabricRt = fabric.transform as RectTransform;
            var layoutRt = _tab.transform as RectTransform;
            if (patternRt == null || fabricRt == null || layoutRt == null)
                return;

            if (_tabsReady)
                return;

            var row = patternRt.parent.GetComponent<HorizontalLayoutGroup>();
            if (row != null)
            {
                if (!_groupTouched)
                {
                    _rowLayout = row;
                    _controlWidth = row.childControlWidth;
                    _expandWidth = row.childForceExpandWidth;
                    _groupTouched = true;
                }
                row.enabled = true;
                row.childControlWidth = true;
                row.childForceExpandWidth = true;
            }

            ShareTab(pattern.gameObject);
            ShareTab(fabric.gameObject);
            ShareTab(_tab);

            DisableLocalize(_tab);
            SetText(_tab, "Layout");
            FitLabel(pattern.gameObject);
            FitLabel(fabric.gameObject);
            FitLabel(_tab);
            _tabsReady = true;
        }

        static void ShareTab(GameObject tab)
        {
            var element = tab.GetComponent<LayoutElement>() ?? tab.AddComponent<LayoutElement>();
            element.flexibleWidth = 1f;
            element.minWidth = 0f;
            element.preferredWidth = -1f;
        }

        static void FitLabel(GameObject tab)
        {
            foreach (var component in tab.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                PropertyInfo text = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (text == null || text.PropertyType != typeof(string) || !text.CanWrite)
                    continue;
                SetBoolProperty(component, "enableWordWrapping", false);
                SetBoolProperty(component, "enableAutoSizing", true);
                SetFloatProperty(component, "fontSizeMin", 10f);
                PropertyInfo size = component.GetType().GetProperty("fontSize", BindingFlags.Instance | BindingFlags.Public);
                float current = 18f;
                if (size != null && size.PropertyType == typeof(float))
                    current = (float)size.GetValue(component, null);
                SetFloatProperty(component, "fontSizeMax", Mathf.Max(15f, current));
                SetEnumProperty(component, "overflowMode", "Overflow");
            }
        }

        static void SetBoolProperty(Component component, string name, bool value)
        {
            PropertyInfo property = component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
                property.SetValue(component, value, null);
        }

        static void SetFloatProperty(Component component, string name, float value)
        {
            PropertyInfo property = component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite && property.PropertyType == typeof(float))
                property.SetValue(component, value, null);
        }

        static void SetEnumProperty(Component component, string name, string value)
        {
            PropertyInfo property = component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite || !property.PropertyType.IsEnum)
                return;
            try
            {
                property.SetValue(component, Enum.Parse(property.PropertyType, value), null);
            }
            catch (Exception)
            {
            }
        }

        static void SetEveryText(GameObject root, string value)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                PropertyInfo property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(string) || !property.CanWrite)
                    continue;
                string current = property.GetValue(component, null) as string;
                if (string.IsNullOrWhiteSpace(current))
                    continue;
                bool letter = false;
                for (int i = 0; i < current.Length; i++)
                {
                    if (!char.IsLetter(current[i]))
                        continue;
                    letter = true;
                    break;
                }
                if (letter)
                    property.SetValue(component, value, null);
            }
        }

        static void RestoreTabs()
        {
            _tabsReady = false;
            if (_groupTouched && _rowLayout != null)
            {
                _rowLayout.childControlWidth = _controlWidth;
                _rowLayout.childForceExpandWidth = _expandWidth;
                _groupTouched = false;
            }
            var sidebar = GameAccess.Find("SidebarInventory") as MonoBehaviour;
            if (sidebar != null)
            {
                Unshare(GameAccess.Member(sidebar, "patternTab") as Component);
                Unshare(GameAccess.Member(sidebar, "fabricTab") as Component);
                Unshare(GameAccess.Member(sidebar, "accessoryTab") as Component);
            }
        }

        static void Unshare(Component tab)
        {
            if (tab == null)
                return;
            var element = tab.GetComponent<LayoutElement>();
            if (element == null)
                return;
            element.flexibleWidth = 0f;
            element.minWidth = -1f;
            element.preferredWidth = -1f;
        }

        static void Hide()
        {
            _open = false;
            if (_page != null)
                _page.SetActive(false);
            if (_tab != null)
                _tab.SetActive(false);
            RestoreTabs();
        }
        
        public static void Refresh()
        {
            _stamp = "";
        }
        static void Rebuild(MonoBehaviour sidebar)
        {
            if (_list == null)
                return;
            object game = GameAccess.Instance("GameManager");
            var fabrics = AutoPlace.Fabrics();
            var patterns = AutoPlace.Patterns(game);
            KeepOwned(fabrics, patterns);
            if (Added.Count == 1 && _replace < 0 && !_adding)
                GiveAll(patterns, Added[0]);
            string stamp = Stamp(fabrics, patterns);
            _stamp = stamp;

            float scroll = _scroll != null ? _scroll.verticalNormalizedPosition : 1f;
            for (int i = _list.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(_list.GetChild(i).gameObject);

            GameObject template = RowTemplate(sidebar);
            if (template == null || fabrics.Count == 0 || Added.Count == 0)
            {
                NoteRow(template, "The fabrics you own show up here.");
                FinishLayout();
                _stamp = stamp.Length > 0 ? stamp : "empty";
                return;
            }

            Board(template, fabrics, patterns);
            Row(template, null, "Reset", "", false, () =>
            {
                AutoPlace.ReturnPieces(patterns);
                _stamp = "";
            }, null);

            Row(template, null, "Solve", "", false, () =>
            {
                if (Added.Count == 0)
                {
                    AutoPlace.SetNotice("Choose a fabric first.");
                    _stamp = "";
                    return;
                }
                object show = AutoPlace.OpenFabric(_room);
                if (!AutoPlace.ContainsFabric(Added, show))
                    show = Added[0];
                AutoPlace.Run(_room, game, new List<object>(Added), patterns, show);
                _stamp = "";
            }, null);

            Row(template, null, "Optimize", "", false, () =>
            {
                if (Added.Count == 0)
                {
                    AutoPlace.SetNotice("Choose a fabric first.");
                    _stamp = "";
                    return;
                }
                object show = AutoPlace.OpenFabric(_room);
                if (!AutoPlace.ContainsFabric(Added, show))
                    show = Added[0];
                AutoPlace.Optimize(_room, game, new List<object>(Added), patterns, show);
                _stamp = "";
            }, null);

            if (!string.IsNullOrEmpty(AutoPlace.Notice))
                NoteRow(template, AutoPlace.Notice);

            FinishLayout();
            FitNames();
            FreezeRows();
            if (_scroll != null)
                _scroll.verticalNormalizedPosition = scroll;
        }

        static void FinishLayout()
        {
            if (_list == null)
                return;
            var fitter = _list.GetComponent<ContentSizeFitter>();
            if (fitter != null)
                fitter.enabled = true;
            LayoutRebuilder.ForceRebuildLayoutImmediate(_list);
            float height = LayoutUtility.GetPreferredHeight(_list);
            _list.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(40f, height));
            if (fitter != null)
                fitter.enabled = false;
        }

        static void KeepOwned(List<object> fabrics, List<object> patterns)
        {
            for (int i = Added.Count - 1; i >= 0; i--)
            {
                if (AutoPlace.ContainsFabric(fabrics, Added[i]))
                    continue;
                object gone = Added[i];
                Added.RemoveAt(i);
                foreach (object panel in patterns)
                {
                    if (ReferenceEquals(AutoPlace.PatternFabric(panel), gone))
                        AutoPlace.SetPatternFabric(panel, null);
                }
            }
            if (Added.Count == 0 && fabrics.Count > 0)
            {
                Added.Add(fabrics[0]);
                GiveAll(patterns, fabrics[0]);
            }
            if (_selected >= Added.Count)
                _selected = 0;
            if (_replace >= Added.Count)
                _replace = -1;
        }

        static void Board(GameObject template, List<object> fabrics, List<object> patterns)
        {
            if (_selected >= Added.Count)
                _selected = 0;
            for (int i = 0; i < Added.Count; i++)
            {
                int index = i;
                object fabric = Added[i];
                string title = (Added.Count > 1 && index == _selected ? "• " : "") + AutoPlace.ShortFabric(fabric);
                Row(template, fabric, title, LengthText(fabric), false, () =>
                {
                    _selected = index;
                    _adding = false;
                    if (Added.Count <= 1)
                    {
                        _replace = -1;
                        _fabricsOpen = !_fabricsOpen;
                    }
                    else
                    {
                        _replace = index;
                        _fabricsOpen = true;
                    }
                    _stamp = "";
                }, null);
            }
            FabricFolder(template, fabrics, patterns);
            CategoryRows(template, patterns);
            if (CanAdd(fabrics))
                AddRow(template);
            if (Added.Count > 1)
                Row(template, null, "Remove fabric", "", false, () => RemoveSelected(patterns), null);
        }

        static void FabricFolder(GameObject template, List<object> fabrics, List<object> patterns)
        {
            bool open = _fabricsOpen || _adding || _replace >= 0;
            Row(template, null, (open ? "v " : "> ") + "Fabrics", Added.Count.ToString(), false, () =>
            {
                _fabricsOpen = !open;
                if (!_fabricsOpen)
                {
                    _adding = false;
                    _replace = -1;
                }
                _stamp = "";
            }, null);
            if (!open)
                return;
            object also = Added.Count > 1 && !_adding && _replace >= 0 && _replace < Added.Count ? Added[_replace] : null;
            int shown = 0;
            foreach (object fabric in Ordered(fabrics))
            {
                if (AutoPlace.ContainsFabric(Added, fabric) && !ReferenceEquals(fabric, also))
                    continue;
                shown++;
                object choose = fabric;
                Row(template, choose, AutoPlace.ShortFabric(choose), LengthText(choose), true, () => PickFabric(choose, patterns), null);
            }
            if (shown == 0)
                NoteRow(template, "Every other fabric is already in use.");
        }

        static void PickFabric(object fabric, List<object> patterns)
        {
            if (fabric == null || (Added.Count == 1 && ReferenceEquals(Added[0], fabric)))
                return;
            if (_adding)
                AddFabric(fabric);
            else if (_replace >= 0 && _replace < Added.Count)
                UseFabric(_replace, fabric, patterns);
            else if (Added.Count <= 1)
                UseFabric(0, fabric, patterns);
            else
                UseFabric(Mathf.Clamp(_selected, 0, Added.Count - 1), fabric, patterns);
            _fabricsOpen = false;
            _stamp = "";
        }

        static void AddRow(GameObject template)
        {
            Row(template, null, "Add fabric", "+", false, () =>
            {
                if (Added.Count >= MaxFabrics)
                    return;
                _adding = true;
                _replace = -1;
                _fabricsOpen = true;
                _stamp = "";
            }, null);
        }

        static bool CanAdd(List<object> fabrics)
        {
            if (Added.Count >= MaxFabrics)
                return false;
            foreach (object fabric in fabrics)
            {
                if (!AutoPlace.ContainsFabric(Added, fabric))
                    return true;
            }
            return false;
        }

        static void CategoryRows(GameObject template, List<object> patterns)
        {
            foreach (string group in Groups)
            {
                string name = group;
                var mine = new List<object>();
                foreach (object panel in patterns)
                {
                    if (GroupOf(panel) == name)
                        mine.Add(panel);
                }
                if (mine.Count == 0)
                    continue;
                bool open = OpenGroups.Contains(name);
                string label = GroupLabel(mine);
                string title = (open ? "v " : "> ") + name;
                Row(template, null, title, label, true, () =>
                {
                    if (!OpenGroups.Add(name))
                        OpenGroups.Remove(name);
                    _stamp = "";
                }, () => CycleGroup(mine), null, label);
                if (!open)
                    continue;
                foreach (object panel in mine)
                    PieceRow(template, panel);
            }
        }

        static void PieceRow(GameObject template, object panel)
        {
            object piece = panel;
            string label = PieceLabel(piece);
            Row(template, null, "    " + AutoPlace.NameOf(piece), label, true, null, () =>
            {
                AutoPlace.SetPatternFabric(piece, NextAssignment(AutoPlace.PatternFabric(piece)));
                _stamp = "";
            }, null, label);
        }

        static void CycleGroup(List<object> group)
        {
            object current = null;
            bool same = true;
            foreach (object panel in group)
            {
                object fabric = AutoPlace.PatternFabric(panel);
                if (current == null)
                    current = fabric;
                else if (!ReferenceEquals(current, fabric))
                    same = false;
            }
            object target = NextAssignment(same ? current : null);
            foreach (object panel in group)
                AutoPlace.SetPatternFabric(panel, target);
            _stamp = "";
        }

        static string GroupLabel(List<object> group)
        {
            object current = null;
            bool any = false;
            bool mixed = false;
            foreach (object panel in group)
            {
                object fabric = AutoPlace.PatternFabric(panel);
                if (fabric == null)
                {
                    if (any)
                        mixed = true;
                    continue;
                }
                any = true;
                if (current == null)
                    current = fabric;
                else if (!ReferenceEquals(current, fabric))
                    mixed = true;
            }
            if (!any || current == null)
                return "";
            return mixed ? "Mixed" : AutoPlace.ShortFabric(current);
        }

        static string PieceLabel(object panel)
        {
            object fabric = AutoPlace.PatternFabric(panel);
            return fabric == null ? "" : AutoPlace.ShortFabric(fabric);
        }

        static object NextAssignment(object current)
        {
            if (Added.Count == 0)
                return null;
            for (int i = 0; i < Added.Count; i++)
            {
                if (ReferenceEquals(Added[i], current))
                    return Added[(i + 1) % Added.Count];
            }
            return Added[0];
        }

        static string GroupOf(object panel)
        {
            object component = GameAccess.Member(panel, "garmentComponent");
            string kind = GameAccess.Member(component, "type")?.ToString() ?? "";
            if (kind == "Skirt")
                return "Skirt";
            if (kind == "Sleeve")
                return "Sleeves";
            if (kind == "Collar")
                return "Collar";
            if (kind == "Bodice")
                return "Blouse";
            string display = AutoPlace.NameOf(panel).ToLowerInvariant();
            if (display.Contains("sleeve"))
                return "Sleeves";
            if (display.Contains("collar"))
                return "Collar";
            if (display.Contains("skirt"))
                return "Skirt";
            if (display.Contains("blouse") || display.Contains("bodice"))
                return "Blouse";
            return "Other";
        }

        static List<object> Ordered(List<object> fabrics)
        {
            var stars = new List<object>();
            var rest = new List<object>();
            foreach (object fabric in fabrics)
            {
                if (AutoPlace.IsStarred(fabric))
                    stars.Add(fabric);
                else
                    rest.Add(fabric);
            }
            stars.AddRange(rest);
            return stars;
        }

        static void GiveAll(List<object> patterns, object fabric)
        {
            if (fabric == null)
                return;
            foreach (object panel in patterns)
            {
                if (!ReferenceEquals(AutoPlace.PatternFabric(panel), fabric))
                    AutoPlace.SetPatternFabric(panel, fabric);
            }
        }

        static void UseFabric(int index, object fabric, List<object> patterns)
        {
            if (index < 0 || index >= Added.Count || fabric == null)
                return;
            object old = Added[index];
            if (!ReferenceEquals(old, fabric) && AutoPlace.ContainsFabric(Added, fabric))
                return;
            Added[index] = fabric;
            if (Added.Count == 1)
                GiveAll(patterns, fabric);
            else if (!ReferenceEquals(old, fabric))
            {
                foreach (object panel in patterns)
                {
                    if (ReferenceEquals(AutoPlace.PatternFabric(panel), old))
                        AutoPlace.SetPatternFabric(panel, fabric);
                }
            }
            _replace = -1;
            _adding = false;
            _stamp = "";
        }

        static void AddFabric(object fabric)
        {
            if (fabric == null || Added.Count >= MaxFabrics || AutoPlace.ContainsFabric(Added, fabric))
                return;
            Added.Add(fabric);
            _selected = Added.Count - 1;
            _adding = false;
            _replace = -1;
            _stamp = "";
        }

        static void RemoveSelected(List<object> patterns)
        {
            if (Added.Count <= 1)
                return;
            int index = Mathf.Clamp(_selected, 0, Added.Count - 1);
            object gone = Added[index];
            Added.RemoveAt(index);
            _selected = Mathf.Clamp(index, 0, Added.Count - 1);
            if (Added.Count == 1)
                GiveAll(patterns, Added[0]);
            else
            {
                foreach (object panel in patterns)
                {
                    if (ReferenceEquals(AutoPlace.PatternFabric(panel), gone))
                        AutoPlace.SetPatternFabric(panel, null);
                }
            }
            _replace = -1;
            _adding = false;
            _stamp = "";
        }

        static string Stamp(List<object> fabrics, List<object> patterns)
        {
            var text = new System.Text.StringBuilder();
            text.Append(_adding).Append('|').Append(_replace).Append('|').Append(_selected).Append('|').Append(_fabricsOpen).Append('|').Append(AutoPlace.Notice);
            foreach (string group in Groups)
            {
                if (OpenGroups.Contains(group))
                    text.Append("|open").Append(group);
            }
            int stars = 0;
            foreach (object fabric in fabrics)
            {
                text.Append('|').Append(AutoPlace.FabricLabel(fabric));
                if (AutoPlace.IsStarred(fabric))
                    stars++;
            }
            text.Append("|stars").Append(stars);
            foreach (object fabric in Added)
                text.Append("|+").Append(AutoPlace.ShortFabric(fabric));
            foreach (object panel in patterns)
            {
                text.Append('|').Append(AutoPlace.NameOf(panel)).Append('=');
                object assigned = AutoPlace.PatternFabric(panel);
                text.Append(assigned == null ? "-" : AutoPlace.ShortFabric(assigned));
            }
            return text.ToString();
        }

        static GameObject RowTemplate(MonoBehaviour sidebar)
        {
            var parent = GameAccess.Member(sidebar, "fabricParent") as Transform;
            if (parent != null && parent.childCount == 0)
                AccessTools.Method(sidebar.GetType(), "RefreshFabrics")?.Invoke(sidebar, null);
            if (parent != null && parent.childCount > 0)
                return parent.GetChild(0).gameObject;
            if (GameAccess.Member(sidebar, "fabricItemPrefab") is Component prefab)
                return prefab.gameObject;
            return null;
        }

        static void Row(GameObject template, object fabric, string title, string side, bool nested, Action left, Action right, Action sideClick = null, string hover = null)
        {
            var row = UnityEngine.Object.Instantiate(template, _list);
            row.name = "LayoutRow";
            Component item = null;
            foreach (var behaviour in row.GetComponents<MonoBehaviour>())
            {
                if (behaviour == null || !HasInitialize(behaviour.GetType()))
                    continue;
                item = behaviour;
                break;
            }
            if (item != null && fabric != null)
                InitializeMethod(item.GetType())?.Invoke(item, new[] { fabric });
            if (item != null && fabric != null)
            {
                try
                {
                    AccessTools.Method(item.GetType(), "SetFavourited")?.Invoke(item, new object[] { AutoPlace.IsStarred(fabric) });
                }
                catch (Exception)
                {
                }
            }
            if (item is Behaviour itemBehaviour)
                itemBehaviour.enabled = false;
            DisableLocalize(row);
            GameAccess.SetMember(item, "OnItemPointerClick", null);
            GameAccess.SetMember(item, "OnItemPointerDown", null);
            GameAccess.SetMember(item, "OnItemPointerEnter", null);
            GameAccess.SetMember(item, "OnItemPointerExit", null);
            row.SetActive(true);
            SetTextPair(row, title, side);
            if (fabric == null)
                WarmBlank(row, item);
            var star = GameAccess.Member(item, "highlightStar") as GameObject;
            if (star != null)
                star.SetActive(fabric != null && AutoPlace.IsStarred(fabric));
            var button = row.GetComponent<Button>();
            if (button != null)
                StripPersistent(button.onClick);
            var image = row.GetComponent<Image>() ?? row.GetComponentInChildren<Image>(true);
            if (image != null)
                image.raycastTarget = true;
            var click = row.GetComponent<LayoutRowClick>() ?? row.AddComponent<LayoutRowClick>();
            click.Left = left;
            click.Right = right;
            click.Hover = string.IsNullOrEmpty(hover) ? null : hover;
            WireSide(row, sideClick);
            var element = row.GetComponent<LayoutElement>() ?? row.AddComponent<LayoutElement>();
            element.ignoreLayout = false;
            element.minHeight = nested ? 32f : 36f;
            element.preferredHeight = nested ? 32f : 36f;
            var rect = row.GetComponent<RectTransform>();
            if (rect != null)
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, element.preferredHeight);
        }

        static void WireSide(GameObject row, Action sideClick)
        {
            if (sideClick == null)
                return;
            Component side = null;
            int seen = 0;
            foreach (var component in row.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                PropertyInfo property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(string))
                    continue;
                seen++;
                if (seen < 2)
                    continue;
                side = component;
                break;
            }
            if (side == null)
                return;
            var graphic = side.GetComponent<Graphic>();
            if (graphic != null)
                graphic.raycastTarget = true;
            var click = side.gameObject.GetComponent<LayoutRowClick>() ?? side.gameObject.AddComponent<LayoutRowClick>();
            click.Left = sideClick;
            click.Right = null;
        }

        static void NoteRow(GameObject template, string message)
        {
            if (template == null || _list == null)
                return;
            var row = UnityEngine.Object.Instantiate(template, _list);
            row.name = "LayoutNote";
            Component item = null;
            foreach (var behaviour in row.GetComponents<MonoBehaviour>())
            {
                if (behaviour != null && item == null && HasInitialize(behaviour.GetType()))
                    item = behaviour;
                if (behaviour != null)
                    behaviour.enabled = false;
            }
            var starObject = GameAccess.Member(item, "highlightStar") as GameObject;
            if (starObject != null)
                starObject.SetActive(false);
            foreach (var raw in row.GetComponentsInChildren<RawImage>(true))
                raw.gameObject.SetActive(false);
            foreach (var image in row.GetComponentsInChildren<Image>(true))
                image.enabled = false;
            foreach (var group in row.GetComponentsInChildren<HorizontalLayoutGroup>(true))
                group.enabled = false;
            foreach (var fit in row.GetComponentsInChildren<ContentSizeFitter>(true))
                fit.enabled = false;
            Component messageText = null;
            int seen = 0;
            foreach (var component in row.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                PropertyInfo property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(string) || !property.CanWrite)
                    continue;
                seen++;
                if (seen == 1)
                    messageText = component;
                else
                    component.gameObject.SetActive(false);
            }
            row.SetActive(true);
            var washGo = new GameObject("LayoutWash", typeof(RectTransform), typeof(LayoutElement), typeof(Image));
            washGo.transform.SetParent(row.transform, false);
            washGo.transform.SetAsFirstSibling();
            var washElement = washGo.GetComponent<LayoutElement>();
            washElement.ignoreLayout = true;
            var washRect = washGo.GetComponent<RectTransform>();
            washRect.anchorMin = Vector2.zero;
            washRect.anchorMax = Vector2.one;
            washRect.offsetMin = new Vector2(2f, 2f);
            washRect.offsetMax = new Vector2(-2f, -2f);
            var wash = washGo.GetComponent<Image>();
            wash.sprite = Pixel();
            wash.color = Parchment;
            wash.raycastTarget = false;
            if (messageText != null)
            {
                messageText.gameObject.SetActive(true);
                messageText.transform.SetParent(row.transform, false);
                messageText.transform.SetAsLastSibling();
                var textRect = messageText.transform as RectTransform;
                if (textRect != null)
                {
                    textRect.anchorMin = Vector2.zero;
                    textRect.anchorMax = Vector2.one;
                    textRect.offsetMin = new Vector2(12f, 8f);
                    textRect.offsetMax = new Vector2(-12f, -8f);
                    textRect.pivot = new Vector2(0f, 1f);
                }
                SetComponentText(messageText, message, true);
                SetEnumProperty(messageText, "alignment", "TopLeft");
                SetBoolProperty(messageText, "enableAutoSizing", false);
                SetFloatProperty(messageText, "fontSize", 15f);
                if (messageText is Graphic graphic)
                {
                    graphic.color = Ink;
                    graphic.raycastTarget = false;
                }
            }
            int lines = 2;
            if (!string.IsNullOrEmpty(message))
                lines = Mathf.Clamp(Mathf.CeilToInt(message.Length / 32f), 2, 6);
            var element = row.GetComponent<LayoutElement>() ?? row.AddComponent<LayoutElement>();
            element.ignoreLayout = false;
            element.minHeight = 18f + lines * 22f;
            element.preferredHeight = element.minHeight;
        }

        static void SetTextPair(GameObject root, string title, string side)
        {
            var texts = new List<Component>();
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                PropertyInfo property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(string) || !property.CanWrite)
                    continue;
                texts.Add(component);
            }
            if (texts.Count == 0)
                return;
            SetComponentText(texts[0], title, false);
            if (texts.Count > 1)
            {
                SetComponentText(texts[1], side, false);
                SetBoolProperty(texts[1], "enableAutoSizing", false);
                SetEnumProperty(texts[1], "alignment", "Right");
            }
        }

        static readonly Color Parchment = new Color(0.80f, 0.67f, 0.50f, 1f);
        static readonly Color Ink = new Color(0.29f, 0.17f, 0.10f, 1f);
        static Sprite _pixel;

        public static Sprite Pixel()
        {
            if (_pixel != null)
                return _pixel;
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, Color.white);
            texture.Apply();
            _pixel = Sprite.Create(texture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            return _pixel;
        }

        public static Component SideText(GameObject row)
        {
            if (row == null)
                return null;
            int seen = 0;
            foreach (var component in row.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                PropertyInfo property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(string))
                    continue;
                seen++;
                if (seen == 2)
                    return component;
            }
            return null;
        }

        public static void ShowHover(GameObject label, string value)
        {
            if (label == null)
                return;
            foreach (var component in label.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                PropertyInfo property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(string) || !property.CanWrite)
                    continue;
                property.SetValue(component, value ?? "", null);
                SetBoolProperty(component, "enableWordWrapping", false);
                SetBoolProperty(component, "enableAutoSizing", false);
                SetFloatProperty(component, "fontSize", 15f);
                SetEnumProperty(component, "overflowMode", "Ellipsis");
                SetEnumProperty(component, "alignment", "Right");
                if (component is Graphic graphic)
                {
                    graphic.color = Ink;
                    graphic.raycastTarget = false;
                }
                return;
            }
        }

        static void WarmBlank(GameObject row, Component item)
        {
            if (row == null)
                return;
            foreach (var raw in row.GetComponentsInChildren<RawImage>(true))
            {
                if (raw != null)
                    raw.gameObject.SetActive(false);
            }
            var background = GameAccess.Member(item, "background") as Image;
            var flash = GameAccess.Member(item, "highlightFlash") as Image;
            foreach (var image in row.GetComponentsInChildren<Image>(true))
            {
                if (image == null || image == background || image == flash)
                    continue;
                Color color = image.color;
                if (color.a < 0.05f || color.r < 0.75f || color.g < 0.75f || color.b < 0.75f)
                    continue;
                image.color = Parchment;
            }
            var washGo = new GameObject("LayoutWash", typeof(RectTransform), typeof(LayoutElement), typeof(Image));
            washGo.transform.SetParent(row.transform, false);
            washGo.transform.SetAsFirstSibling();
            var washElement = washGo.GetComponent<LayoutElement>();
            washElement.ignoreLayout = true;
            var washRect = washGo.GetComponent<RectTransform>();
            washRect.anchorMin = Vector2.zero;
            washRect.anchorMax = Vector2.one;
            washRect.offsetMin = new Vector2(2f, 4f);
            washRect.offsetMax = new Vector2(-4f, -4f);
            var wash = washGo.GetComponent<Image>();
            wash.sprite = Pixel();
            wash.color = Parchment;
            wash.raycastTarget = false;
            Component side = SideText(row);
            if (side is Graphic graphic)
                graphic.color = Ink;
            if (side == null)
                return;
            var sideElement = side.gameObject.GetComponent<LayoutElement>() ?? side.gameObject.AddComponent<LayoutElement>();
            sideElement.minWidth = 72f;
            sideElement.preferredWidth = 112f;
            sideElement.flexibleWidth = 0f;
        }

        static void FitNames()
        {
            if (_list == null)
                return;
            for (int i = 0; i < _list.childCount; i++)
            {
                var row = _list.GetChild(i).gameObject;
                var click = row.GetComponent<LayoutRowClick>();
                if (click == null || string.IsNullOrEmpty(click.Hover))
                    continue;
                float left = NameLeft(row);
                foreach (var group in row.GetComponentsInChildren<HorizontalLayoutGroup>(true))
                    group.enabled = false;
                foreach (var fit in row.GetComponentsInChildren<ContentSizeFitter>(true))
                    fit.enabled = false;
                var side = SideText(row);
                var rect = side == null ? null : side.transform as RectTransform;
                if (rect == null)
                    continue;
                rect.SetParent(row.transform, false);
                var element = rect.GetComponent<LayoutElement>() ?? rect.gameObject.AddComponent<LayoutElement>();
                element.ignoreLayout = true;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = new Vector2(left, 2f);
                rect.offsetMax = new Vector2(-8f, -2f);
                rect.SetAsLastSibling();
                ShowHover(rect.gameObject, click.Hover);
            }
        }

        static void FreezeRows()
        {
            if (_list == null)
                return;
            for (int i = 0; i < _list.childCount; i++)
            {
                foreach (var behaviour in _list.GetChild(i).GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || behaviour is LayoutRowClick || behaviour is Graphic || behaviour is LayoutElement)
                        continue;
                    // The row has already been measured and positioned. Cloned
                    // inventory layout and animation scripts need no Update loop.
                    behaviour.enabled = false;
                }
                foreach (var component in _list.GetChild(i).GetComponentsInChildren<Component>(true))
                {
                    if (component == null)
                        continue;
                    SetBoolProperty(component, "enableAutoSizing", false);
                }
            }
        }

        public static Component FirstText(GameObject row)
        {
            if (row == null)
                return null;
            foreach (var component in row.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                PropertyInfo property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(string))
                    continue;
                return component;
            }
            return null;
        }

        public static float NameLeft(GameObject row)
        {
            var rowRect = row == null ? null : row.transform as RectTransform;
            var titleRect = FirstText(row) == null ? null : FirstText(row).transform as RectTransform;
            if (rowRect == null || titleRect == null)
                return 108f;
            float width = rowRect.rect.width;
            if (width < 40f)
                return 108f;
            var corners = new Vector3[4];
            titleRect.GetWorldCorners(corners);
            float local = rowRect.InverseTransformPoint(corners[2]).x - rowRect.rect.xMin;
            return Mathf.Clamp(local + 8f, 84f, width * 0.58f);
        }

        static void SetComponentText(Component component, string value, bool wrap)
        {
            PropertyInfo property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            property?.SetValue(component, value ?? "", null);
            PropertyInfo wrapping = component.GetType().GetProperty("enableWordWrapping", BindingFlags.Instance | BindingFlags.Public);
            if (wrapping != null && wrapping.CanWrite && wrapping.PropertyType == typeof(bool))
                wrapping.SetValue(component, wrap, null);
            SetEnumProperty(component, "overflowMode", wrap ? "Masking" : "Ellipsis");
        }

        static void SetText(GameObject root, string value)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                PropertyInfo property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(string) || !property.CanWrite)
                    continue;
                string current = property.GetValue(component, null) as string;
                if (string.IsNullOrWhiteSpace(current))
                    continue;
                property.SetValue(component, value, null);
                return;
            }
        }

        static void SetTabState(object tab, bool selected)
        {
            var component = tab as Component;
            if (component == null && tab is GameObject game)
                component = game.GetComponent<MonoBehaviour>();
            if (component == null)
                return;
            AccessTools.Method(component.GetType(), "SetState")?.Invoke(component, new object[] { selected });
        }

        static void SetActive(object target, bool active)
        {
            if (target is Component component)
                component.gameObject.SetActive(active);
            else if (target is GameObject game)
                game.SetActive(active);
        }

        static void DisableLocalize(GameObject root)
        {
            foreach (var behaviour in root.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour != null && behaviour.GetType().Name.IndexOf("Localize", StringComparison.OrdinalIgnoreCase) >= 0)
                    behaviour.enabled = false;
            }
        }

        static string LengthText(object piece)
        {
            return GameAccess.Float(piece, "length", 0f).ToString("0.00");
        }

        static void StripPersistent(UnityEventBase evt)
        {
            if (evt == null)
                return;
            try
            {
                FieldInfo field = typeof(UnityEventBase).GetField("m_PersistentCalls", BindingFlags.Instance | BindingFlags.NonPublic);
                object group = field?.GetValue(evt);
                MethodInfo clear = group?.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (clear != null)
                    clear.Invoke(group, null);
            }
            catch (Exception)
            {
            }
            evt.RemoveAllListeners();
        }
    }

    public class LayoutRowClick : MonoBehaviour, IPointerClickHandler
    {
        public Action Left;
        public Action Right;
        public string Hover;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Right)
            {
                if (Right != null)
                    Right();
                return;
            }
            if (Left != null)
                Left();
        }
    }
}
