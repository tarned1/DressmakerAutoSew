using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DressmakerAutoSew
{
    public static class PauseMenuOptions
    {
        const string ButtonName = "AutoSewMenuButton";
        const string PanelName = "AutoSewPanel";
        const string ToggleName = "AutoSewToggle";
        const string SliderName = "AutoSewSpeedSlider";

        static readonly string[] SwitchNames = { "AutoMeasureToggle", "AutoMannequinToggle", "PlacePatternsToggle" };
        static readonly string[] SwitchLabels = { "Auto measure (F4)", "Auto mannequin (F4)", "Place patterns" };

        static GameObject _stopRow;
        static Toggle _toggle;
        static Slider _slider;
        static GameObject _sliderRow;
        static GameObject _panel;
        static readonly Toggle[] _switches = new Toggle[3];
        static bool _refreshing;
        static bool _buttonPlaced;

        public static void OnEnablePostfix(MonoBehaviour __instance)
        {
            try
            {
                Dismiss();
                Ensure(__instance);
                Dismiss();
                Refresh();
            }
            catch (Exception ex)
            {
                Dismiss();
                Plugin.Log.LogWarning("Could not add the automation pause menu: " + ex.Message);
            }
        }

        // Esc closes the whole pause menu and does not press the dialog's close button.
        public static void HidePostfix()
        {
            Dismiss();
        }

        public static void Refresh()
        {
            if (_refreshing || _panel == null || !_panel.activeInHierarchy)
                return;
            _refreshing = true;
            try
            {
                if (_toggle != null)
                    _toggle.SetIsOnWithoutNotify(Plugin.Enabled);
                if (_slider != null)
                {
                    _slider.minValue = Plugin.SpeedMin;
                    _slider.maxValue = Plugin.SpeedMax;
                    _slider.wholeNumbers = false;
                    _slider.SetValueWithoutNotify(Plugin.Speed);
                }
                for (int i = 0; i < _switches.Length; i++)
                {
                    if (_switches[i] != null)
                        _switches[i].SetIsOnWithoutNotify(SwitchValue(i));
                }
                if (_stopRow != null)
                    _stopRow.SetActive(Plugin.ManualAutomationActive);
                ApplySpeedVisibility();
            }
            finally
            {
                _refreshing = false;
            }
        }

        static void Ensure(MonoBehaviour pause)
        {
            var assist = AccessTools.Field(pause.GetType(), "sewingAssistToggle")?.GetValue(pause) as Toggle;
            var music = AccessTools.Field(pause.GetType(), "musicVolumeSlider")?.GetValue(pause) as Slider;
            if (assist == null)
                return;

            Transform list = assist.transform.parent;
            if (list != null && list.parent != null && list.childCount <= 8)
                list = list.parent;

            Transform language = FindLanguageButton(pause.transform);
            var popup = AccessTools.Field(pause.GetType(), "languagePopup")?.GetValue(pause) as Component;
            EnsureDialog(popup, assist, music);
            EnsureButton(language);
            PauseListScroll.Install(list);
        }

        static Component _languagePopup;
        static Component _menuType;
        static GameObject _dimmer;
        static Sprite _paperSprite;
        static float _lockedWidth;
        static float _chrome = 90f;
        static bool _chromeReady;

        // The language button opens LanguageSelectorPopup, a dialog of its own. Automation uses a copy of that dialog.
        static void EnsureDialog(Component popup, Toggle assist, Slider music)
        {
            if (popup == null)
            {
                Plugin.Log.LogWarning("Language popup was not found, so the automation menu was not added.");
                return;
            }
            _languagePopup = popup;
            Transform parent = popup.transform.parent;
            if (parent == null)
                parent = popup.transform;
            Transform existing = FindNamed(parent, PanelName);
            if (existing == null)
            {
                GameObject root = UnityEngine.Object.Instantiate(popup.gameObject, parent, false);
                root.name = PanelName;
                var behaviour = root.GetComponent(popup.GetType()) as Behaviour;
                Transform holder = null;
                if (behaviour != null)
                {
                    holder = AccessTools.Field(popup.GetType(), "buttonHolder")?.GetValue(behaviour) as Transform;
                    behaviour.enabled = false;
                }
                _menuType = MenuType(popup);
                Retitle(root, holder);
                if (holder == null)
                    holder = root.transform;
                for (int i = holder.childCount - 1; i >= 0; i--)
                    holder.GetChild(i).gameObject.SetActive(false);
                PrepareHolder(holder);
                ConstrainWidth(holder);
                AddPaper(holder);
                BuildControls(holder, assist, music);
                WireClose(root);
                _dimmer = LiftDimmer(root, holder);
                if (root.GetComponent<AutoSewGuard>() == null)
                    root.AddComponent<AutoSewGuard>();
                root.SetActive(false);
                HideDimmer();
                existing = root.transform;
            }
            else
            {
                BindExisting(existing);
                var canvas = existing.GetComponentInParent<Canvas>();
                _dimmer = canvas != null ? FindNamed(canvas.transform, "AutoSewDimmer")?.gameObject : null;
                if (existing.GetComponent<AutoSewGuard>() == null)
                    existing.gameObject.AddComponent<AutoSewGuard>();
            }
            _panel = existing.gameObject;
        }

        // Language darkens the whole screen. That shade is a child of the popup, so shrinking the popup squashes it into a band.
        static GameObject LiftDimmer(GameObject root, Transform holder)
        {
            Transform dimmer = FindNamed(root.transform, "LanguageBacking");
            if (dimmer == null)
            {
                foreach (Image image in root.GetComponentsInChildren<Image>(true))
                {
                    if (image.transform == root.transform)
                        continue;
                    if (holder != null && (image.transform == holder || holder.IsChildOf(image.transform)))
                        continue;
                    string name = image.name;
                    bool named = name.IndexOf("Backing", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Dim", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Blocker", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Overlay", StringComparison.OrdinalIgnoreCase) >= 0;
                    var rect = image.rectTransform;
                    bool stretch = rect.anchorMin.x <= 0.02f && rect.anchorMin.y <= 0.02f
                        && rect.anchorMax.x >= 0.98f && rect.anchorMax.y >= 0.98f;
                    bool translucent = image.color.a >= 0.2f && image.color.a < 0.97f;
                    if (named || (stretch && translucent && image.transform.parent == root.transform))
                    {
                        dimmer = image.transform;
                        break;
                    }
                }
            }
            if (dimmer == null)
                return null;
            var canvas = root.GetComponentInParent<Canvas>();
            Transform host = canvas != null ? canvas.transform : root.transform.parent;
            if (host == null)
                return null;
            dimmer.SetParent(host, false);
            dimmer.name = "AutoSewDimmer";
            var dimmerRect = dimmer as RectTransform;
            if (dimmerRect != null)
            {
                dimmerRect.anchorMin = Vector2.zero;
                dimmerRect.anchorMax = Vector2.one;
                dimmerRect.pivot = new Vector2(0.5f, 0.5f);
                dimmerRect.anchoredPosition = Vector2.zero;
                dimmerRect.sizeDelta = Vector2.zero;
                dimmerRect.offsetMin = Vector2.zero;
                dimmerRect.offsetMax = Vector2.zero;
            }
            var imageOnDimmer = dimmer.GetComponent<Image>();
            if (imageOnDimmer != null)
                imageOnDimmer.raycastTarget = true;
            dimmer.SetSiblingIndex(root.transform.GetSiblingIndex());
            return dimmer.gameObject;
        }

        static RectTransform FrameOf(Transform root, Transform holder)
        {
            if (root == null)
                return null;
            if (holder == null || holder == root || holder.parent == null)
                return root as RectTransform;
            Transform walk = holder;
            while (walk.parent != null && walk.parent != root)
                walk = walk.parent;
            if (walk == null || walk == root || walk.name == "AutoSewDimmer" || walk.name == "LanguageBacking")
                return root as RectTransform;
            return walk as RectTransform;
        }

        static void PrepareHolder(Transform holder)
        {
            if (holder.GetComponent<HorizontalOrVerticalLayoutGroup>() != null || holder.GetComponent<GridLayoutGroup>() != null)
                return;
            var layout = holder.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fit = holder.gameObject.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        static void Retitle(GameObject root, Transform holder)
        {
            Transform title = FindNamed(root.transform, "TitleText");
            if (title != null && (holder == null || !title.IsChildOf(holder)))
            {
                DisableLocalizers(title.gameObject);
                SetText(title.gameObject, "Automation");
                FitTitle(title);
                return;
            }
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                if (holder != null && (component.transform == holder || component.transform.IsChildOf(holder)))
                    continue;
                var property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(string) || !property.CanWrite)
                    continue;
                string current = property.GetValue(component) as string;
                if (current == null || current.IndexOf("Language", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (component is Behaviour behaviour
                    && component.GetType().Name.IndexOf("Localize", StringComparison.OrdinalIgnoreCase) >= 0)
                    behaviour.enabled = false;
                DisableLocalizers(component.gameObject);
                property.SetValue(component, "Automation");
            }
        }

        static void DisableLocalizers(GameObject root)
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component is Behaviour behaviour
                    && component.GetType().Name.IndexOf("Localize", StringComparison.OrdinalIgnoreCase) >= 0)
                    behaviour.enabled = false;
            }
        }

        static void WireClose(GameObject root)
        {
            Transform close = FindNamed(root.transform, "CloseButton");
            if (close == null)
                return;
            var button = close.GetComponent<Button>() ?? close.GetComponentInChildren<Button>(true);
            if (button != null)
            {
                StripPersistent(button.onClick);
                button.onClick.AddListener(Close);
                if (_sliderRow != null)
                {
                    _stopRow = UnityEngine.Object.Instantiate(button.gameObject, _sliderRow.transform.parent);
                    _stopRow.name = "AutoSewStopManual";
                    AdoptRow(_stopRow);
                    var stop = _stopRow.GetComponent<Button>() ?? _stopRow.GetComponentInChildren<Button>(true);
                    StripPersistent(stop.onClick);
                    stop.onClick.AddListener(Plugin.StopManualAutomation);
                    ForceLabel(_stopRow, "Stop (F4)", null);
                    StyleRow(_stopRow);
                    _stopRow.SetActive(false);
                }
            }
        }

        static Component MenuType(Component popup)
        {
            var prefab = AccessTools.Field(popup.GetType(), "buttonPrefab")?.GetValue(popup) as Component;
            if (prefab == null)
                return null;
            foreach (Component component in prefab.GetComponentsInChildren<Component>(true))
            {
                if (HasText(component))
                    return component;
            }
            return null;
        }

        // Language entries are drawn in that popup's own type. The pause-menu labels are a faint brown sized for a light page.
        static void StyleRow(GameObject row)
        {
            float size = 30f;
            object font = null;
            object material = null;
            if (_menuType != null)
            {
                var sampleType = _menuType.GetType();
                var sizeProperty = sampleType.GetProperty("fontSize", BindingFlags.Instance | BindingFlags.Public);
                if (sizeProperty != null && sizeProperty.PropertyType == typeof(float))
                    size = Mathf.Clamp((float)sizeProperty.GetValue(_menuType), 26f, 34f);
                var fontProperty = sampleType.GetProperty("font", BindingFlags.Instance | BindingFlags.Public);
                font = fontProperty != null ? fontProperty.GetValue(_menuType) : null;
                if (font != null)
                {
                    var fontMaterial = font.GetType().GetProperty("material", BindingFlags.Instance | BindingFlags.Public);
                    material = fontMaterial != null ? fontMaterial.GetValue(font) : null;
                }
            }
            var ink = new Color(0.33f, 0.2f, 0.14f, 1f);
            foreach (Component component in row.GetComponentsInChildren<Component>(true))
            {
                if (!HasText(component))
                    continue;
                string current = TextOf(component);
                if (string.IsNullOrWhiteSpace(current) || !HasLetter(current))
                    continue;
                var type = component.GetType();
                SetProperty(type, component, "font", font);
                SetProperty(type, component, "fontSharedMaterial", material);
                SetProperty(type, component, "enableAutoSizing", false);
                SetProperty(type, component, "fontSize", size);
                SetProperty(type, component, "fontStyle", Enum.ToObject(type.GetProperty("fontStyle")?.PropertyType ?? typeof(int), 0));
                if (component is Graphic graphic)
                    graphic.color = ink;
                SetProperty(type, component, "faceColor", ink);
            }
        }

        static void SetProperty(Type type, Component component, string name, object value)
        {
            if (value == null)
                return;
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite || !property.PropertyType.IsInstanceOfType(value))
                return;
            property.SetValue(component, value);
        }

        static void FitTitle(Transform title)
        {
            foreach (Component component in title.GetComponentsInChildren<Component>(true))
            {
                var type = component.GetType();
                var sizeProperty = type.GetProperty("fontSize", BindingFlags.Instance | BindingFlags.Public);
                if (sizeProperty == null || sizeProperty.PropertyType != typeof(float) || !sizeProperty.CanWrite)
                    continue;
                float size = (float)sizeProperty.GetValue(component);
                var auto = type.GetProperty("enableAutoSizing", BindingFlags.Instance | BindingFlags.Public);
                if (auto != null && auto.CanWrite && auto.PropertyType == typeof(bool))
                    auto.SetValue(component, false);
                sizeProperty.SetValue(component, size * 0.82f);
            }
            var rect = title as RectTransform ?? title.GetComponent<RectTransform>();
            if (rect != null)
                rect.anchoredPosition += new Vector2(0f, -6f);
        }

        static void AddPaper(Transform holder)
        {
            Transform host = holder.parent;
            var scroll = holder.GetComponentInParent<ScrollRect>();
            if (scroll != null && scroll.viewport != null)
                host = scroll.viewport;
            if (host == null)
                return;
            var paper = new GameObject("AutoSewPaper", typeof(RectTransform), typeof(Image));
            paper.transform.SetParent(host, false);
            paper.transform.SetAsFirstSibling();
            var image = paper.GetComponent<Image>();
            image.sprite = PaperSprite();
            image.type = Image.Type.Simple;
            image.color = new Color(0.94f, 0.88f, 0.78f, 1f);
            image.raycastTarget = false;
            var layout = paper.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            var rect = paper.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.offsetMin = new Vector2(10f, 10f);
            rect.offsetMax = new Vector2(-10f, -8f);
        }

        static Sprite PaperSprite()
        {
            if (_paperSprite != null)
                return _paperSprite;
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, Color.white);
            texture.Apply();
            _paperSprite = Sprite.Create(texture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            return _paperSprite;
        }

        static bool HasText(Component component)
        {
            if (component == null)
                return false;
            var property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            return property != null && property.PropertyType == typeof(string) && property.CanWrite;
        }

        static string TextOf(Component component)
        {
            var property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            return property != null ? property.GetValue(component) as string : null;
        }

        static bool HasLetter(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsLetter(value[i]))
                    return true;
            }
            return false;
        }

        static void BuildControls(Transform content, Toggle assist, Slider music)
        {
            _toggle = BuildToggle(content, assist, ToggleName, "Auto sew", value => Plugin.SetEnabled(value));
            if (music != null)
                _slider = BuildSlider(content, music);
            for (int i = 0; i < SwitchNames.Length; i++)
            {
                int index = i;
                _switches[i] = BuildToggle(content, assist, SwitchNames[i], SwitchLabels[i], value => SetSwitch(index, value));
            }
        }

        static void BindExisting(Transform panel)
        {
            _stopRow = FindNamed(panel, "AutoSewStopManual")?.gameObject;
            Transform toggleRow = FindNamed(panel, ToggleName);
            _toggle = toggleRow != null ? toggleRow.GetComponent<Toggle>() ?? toggleRow.GetComponentInChildren<Toggle>(true) : null;
            Transform sliderRow = FindNamed(panel, SliderName);
            _sliderRow = sliderRow != null ? sliderRow.gameObject : null;
            _slider = sliderRow != null ? sliderRow.GetComponent<Slider>() ?? sliderRow.GetComponentInChildren<Slider>(true) : null;
            for (int i = 0; i < SwitchNames.Length; i++)
            {
                Transform row = FindNamed(panel, SwitchNames[i]);
                _switches[i] = row != null ? row.GetComponent<Toggle>() ?? row.GetComponentInChildren<Toggle>(true) : null;
            }
        }

        static Toggle BuildToggle(Transform parent, Toggle source, string name, string label, UnityAction<bool> changed)
        {
            GameObject row = CloneInto(source, parent, name);
            AdoptRow(row);
            foreach (Toggle extra in row.GetComponentsInChildren<Toggle>(true))
                StripPersistent(extra.onValueChanged);
            var toggle = row.GetComponent<Toggle>() ?? row.GetComponentInChildren<Toggle>(true);
            ForceLabel(row, label, null);
            StyleRow(row);
            toggle.onValueChanged.AddListener(value =>
            {
                if (_refreshing)
                    return;
                changed(value);
            });
            return toggle;
        }

        static Slider BuildSlider(Transform parent, Slider source)
        {
            GameObject row = CloneInto(source, parent, SliderName);
            AdoptRow(row);
            foreach (Slider extra in row.GetComponentsInChildren<Slider>(true))
                StripPersistent(extra.onValueChanged);
            var slider = row.GetComponent<Slider>() ?? row.GetComponentInChildren<Slider>(true);
            slider.minValue = Plugin.SpeedMin;
            slider.maxValue = Plugin.SpeedMax;
            slider.wholeNumbers = false;
            _sliderRow = row;
            ForceLabel(row, "Speed", null);
            StyleRow(row);
            RemoveReadout(row.transform);
            slider.onValueChanged.AddListener(value =>
            {
                if (_refreshing)
                    return;
                Plugin.SetSpeed(value, saveNow: true);
            });
            return slider;
        }

        static void EnsureButton(Transform language)
        {
            if (language == null || language.parent == null)
            {
                Plugin.Log.LogWarning("Language button was not found, so Automation was not added.");
                return;
            }

            Transform parent = language.parent;
            Transform existing = FindNamed(parent, ButtonName);
            if (existing != null)
            {
                _buttonPlaced = true;
                return;
            }
            _buttonPlaced = false;

            GameObject row = UnityEngine.Object.Instantiate(language.gameObject, parent);
            row.name = ButtonName;
            row.SetActive(true);
            StripClick(row);
            var button = row.GetComponent<Button>() ?? row.GetComponentInChildren<Button>(true);
            if (button == null)
                button = row.AddComponent<Button>();
            ForceLabel(row, "Automation", null);
            button.onClick.AddListener(Open);
            PlaceBesideLanguage(row.GetComponent<RectTransform>(), language as RectTransform);
            Plugin.Log.LogInfo("Automation opens from the pause menu.");
        }

        // Language is a normal pause-menu row. Copy that row and open a slot under it.
        // Do not clone a parent, or the dialog title bar is what gets placed over the other rows.
        static void PlaceBesideLanguage(RectTransform button, RectTransform language)
        {
            if (button == null || language == null || _buttonPlaced)
                return;
            _buttonPlaced = true;
            if (UsesLayout(language.parent))
            {
                button.SetSiblingIndex(language.GetSiblingIndex() + 1);
                return;
            }

            button.anchorMin = language.anchorMin;
            button.anchorMax = language.anchorMax;
            button.pivot = language.pivot;
            button.sizeDelta = language.sizeDelta;
            button.localScale = language.localScale;
            button.localRotation = language.localRotation;

            float gap = ButtonGap(language);
            float anchorY = language.anchoredPosition.y;
            for (int i = 0; i < language.parent.childCount; i++)
            {
                var sibling = language.parent.GetChild(i) as RectTransform;
                if (!SameRow(sibling, language) || sibling == button)
                    continue;
                if (sibling.anchoredPosition.y < anchorY - 1f)
                    sibling.anchoredPosition += new Vector2(0f, -gap);
            }

            button.anchoredPosition = language.anchoredPosition + new Vector2(0f, -gap);
            button.SetSiblingIndex(language.GetSiblingIndex() + 1);
        }

        static float ButtonGap(RectTransform language)
        {
            float nearest = 0f;
            float y = language.anchoredPosition.y;
            for (int i = 0; i < language.parent.childCount; i++)
            {
                var sibling = language.parent.GetChild(i) as RectTransform;
                if (!SameRow(sibling, language))
                    continue;
                float distance = Mathf.Abs(sibling.anchoredPosition.y - y);
                if (distance < 1f)
                    continue;
                if (nearest < 1f || distance < nearest)
                    nearest = distance;
            }
            return nearest > 1f ? nearest : RowHeight(language) + 6f;
        }

        static bool SameRow(RectTransform sibling, RectTransform language)
        {
            if (sibling == null || sibling == language || !sibling.gameObject.activeInHierarchy)
                return false;
            if (sibling.name == ButtonName || sibling.name == PanelName || sibling.name == "AutoSewPauseBar")
                return false;
            if (sibling.GetComponent<Button>() == null && sibling.GetComponentInChildren<Button>(true) == null)
                return false;
            float height = RowHeight(sibling);
            float languageHeight = RowHeight(language);
            if (height > languageHeight * 1.8f || height < languageHeight * 0.45f)
                return false;
            float width = sibling.rect.width;
            float languageWidth = language.rect.width;
            if (width < 8f)
                width = sibling.sizeDelta.x;
            if (languageWidth < 8f)
                languageWidth = language.sizeDelta.x;
            if (languageWidth > 8f && width > 8f && (width > languageWidth * 1.35f || width < languageWidth * 0.55f))
                return false;
            return true;
        }

        static Transform FindLanguageButton(Transform scope)
        {
            if (scope == null)
                return null;
            Transform direct = scope.Find("Menu/Language");
            if (IsLanguageButton(direct))
                return direct;
            return FindLanguageWalk(scope);
        }

        static Transform FindLanguageWalk(Transform scope)
        {
            if (IsLanguageButton(scope))
                return scope;
            for (int i = 0; i < scope.childCount; i++)
            {
                Transform found = FindLanguageWalk(scope.GetChild(i));
                if (found != null)
                    return found;
            }
            return null;
        }

        static bool IsLanguageButton(Transform candidate)
        {
            if (candidate == null || candidate.name != "Language")
                return false;
            if (candidate.GetComponent<Button>() == null && candidate.GetComponentInChildren<Button>(true) == null)
                return false;
            for (Transform walk = candidate; walk != null; walk = walk.parent)
            {
                if (walk.name == "LanguageDialog" || walk.name == "LanguageBacking")
                    return false;
            }
            return true;
        }

        public static void LanguageShown()
        {
            Dismiss();
        }

        public static void HideDimmer()
        {
            if (_dimmer == null)
                return;
            var image = _dimmer.GetComponent<Image>();
            if (image != null)
                image.raycastTarget = false;
            _dimmer.SetActive(false);
        }

        static void Dismiss()
        {
            if (_panel != null)
                _panel.SetActive(false);
            HideDimmer();
        }

        static void Open()
        {
            if (_panel == null)
                return;
            if (_languagePopup != null)
                _languagePopup.gameObject.SetActive(false);
            if (_dimmer != null)
            {
                var image = _dimmer.GetComponent<Image>();
                if (image != null)
                    image.raycastTarget = true;
                _dimmer.SetActive(true);
                _dimmer.transform.SetAsLastSibling();
            }
            _panel.SetActive(true);
            _panel.transform.SetAsLastSibling();
            Refresh();
            FitDialog();
        }

        static void Close()
        {
            Dismiss();
        }

        static void ApplySpeedVisibility()
        {
            if (_sliderRow == null)
                return;
            bool show = Plugin.Enabled;
            if (_sliderRow.activeSelf != show)
                _sliderRow.SetActive(show);
            var element = _sliderRow.GetComponent<LayoutElement>();
            if (element != null)
                element.ignoreLayout = !show;
            var parent = _sliderRow.transform.parent as RectTransform;
            if (parent != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(parent);
            FitDialog();
        }

        // Speed showing or hiding changes the height only. The width stays at the dialog's original width.
        static void FitDialog()
        {
            if (_panel == null)
                return;
            var root = _panel.GetComponent<RectTransform>();
            if (root == null || _sliderRow == null)
                return;
            var holder = _sliderRow.transform.parent as RectTransform;
            if (holder == null)
                return;
            var frame = FrameOf(root, holder);
            if (frame == null)
                frame = root;
            if (_lockedWidth < 1f)
                _lockedWidth = frame.rect.width;
            if (!_chromeReady && frame.rect.height > 1f)
            {
                var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(frame, holder);
                float top = frame.rect.yMax - bounds.max.y;
                float bottom = bounds.min.y - frame.rect.yMin;
                _chrome = Mathf.Clamp(top, 28f, 160f) + Mathf.Clamp(bottom, 16f, 120f);
                _chromeReady = true;
            }
            if (_lockedWidth < 1f)
                return;
            float height = _chrome + VisibleContentHeight(holder);
            foreach (ContentSizeFitter fitter in frame.GetComponents<ContentSizeFitter>())
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            var guard = frame.GetComponent<LayoutElement>() ?? frame.gameObject.AddComponent<LayoutElement>();
            guard.preferredWidth = _lockedWidth;
            guard.flexibleWidth = 0f;
            frame.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, _lockedWidth);
            frame.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        }

        static float VisibleContentHeight(RectTransform holder)
        {
            var layout = holder.GetComponent<VerticalLayoutGroup>();
            float spacing = layout != null ? layout.spacing : 8f;
            float sum = layout != null ? layout.padding.top + layout.padding.bottom : 0f;
            int count = 0;
            for (int i = 0; i < holder.childCount; i++)
            {
                var child = holder.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeSelf)
                    continue;
                var element = child.GetComponent<LayoutElement>();
                if (element != null && element.ignoreLayout)
                    continue;
                sum += RowHeight(child);
                count++;
            }
            if (count > 1)
                sum += spacing * (count - 1);
            return sum;
        }

        static void ConstrainWidth(Transform holder)
        {
            var layout = holder.GetComponent<VerticalLayoutGroup>();
            if (layout == null)
                layout = holder.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            foreach (ContentSizeFitter fitter in holder.GetComponentsInChildren<ContentSizeFitter>(true))
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        }

        static bool SwitchValue(int index)
        {
            if (index == 0)
                return Plugin.MeasureEnabled;
            if (index == 1)
                return Plugin.MannequinEnabled;
            return Plugin.PlaceEnabled;
        }

        static void SetSwitch(int index, bool value)
        {
            if (index == 0)
                Plugin.SetMeasureEnabled(value);
            else if (index == 1)
                Plugin.SetMannequinEnabled(value);
            else
                Plugin.SetPlaceEnabled(value);
        }

        static GameObject CloneInto(Component source, Transform parent, string name)
        {
            Transform row = source.transform.parent;
            bool cloneRow = row != null && row.parent != null && row.childCount <= 8;
            GameObject template = cloneRow ? row.gameObject : source.gameObject;
            var clone = UnityEngine.Object.Instantiate(template, parent);
            clone.name = name;
            clone.SetActive(true);
            return clone;
        }

        static void AdoptRow(GameObject row)
        {
            var rect = row.GetComponent<RectTransform>();
            float height = RowHeight(rect);
            if (rect != null)
            {
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.sizeDelta = new Vector2(0f, height);
                rect.anchoredPosition = Vector2.zero;
            }
            var layout = row.GetComponent<LayoutElement>() ?? row.AddComponent<LayoutElement>();
            layout.minHeight = height;
            layout.preferredHeight = height;
            layout.preferredWidth = -1f;
            layout.flexibleWidth = 0f;
            layout.ignoreLayout = false;
            var horizontal = row.GetComponent<HorizontalLayoutGroup>();
            if (horizontal != null)
            {
                horizontal.childControlWidth = true;
                horizontal.childForceExpandWidth = true;
            }
            foreach (LayoutElement child in row.GetComponentsInChildren<LayoutElement>(true))
            {
                if (child == layout)
                    continue;
                if (child.preferredWidth > 240f)
                    child.preferredWidth = -1f;
                if (child.minWidth > 240f)
                    child.minWidth = -1f;
            }
        }

        static void SetText(GameObject root, string value)
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                var property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(string) || !property.CanWrite)
                    continue;
                property.SetValue(component, value);
            }
        }

        static bool UsesLayout(Transform parent)
        {
            if (parent == null)
                return false;
            var vertical = parent.GetComponent<VerticalLayoutGroup>();
            if (vertical != null && vertical.enabled)
                return true;
            var grid = parent.GetComponent<GridLayoutGroup>();
            return grid != null && grid.enabled;
        }

        static float RowHeight(RectTransform rect)
        {
            if (rect == null)
                return 40f;
            float height = rect.rect.height;
            if (height < 8f)
                height = rect.sizeDelta.y;
            if (height < 8f)
                height = 40f;
            return height;
        }

        static void ForceLabel(GameObject root, string label, Transform except)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                if (except != null && (component.transform == except || component.transform.IsChildOf(except)))
                    continue;
                if (component is Behaviour behaviour
                    && component.GetType().Name.IndexOf("Localize", StringComparison.OrdinalIgnoreCase) >= 0)
                    behaviour.enabled = false;

                var property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(string) || !property.CanWrite)
                    continue;
                string current = property.GetValue(component) as string;
                if (string.IsNullOrWhiteSpace(current))
                    continue;
                bool hasLetter = false;
                for (int i = 0; i < current.Length; i++)
                {
                    if (char.IsLetter(current[i]))
                    {
                        hasLetter = true;
                        break;
                    }
                }
                if (hasLetter)
                    property.SetValue(component, label);
            }
        }

        static Transform FindNamed(Transform root, string name)
        {
            if (root == null)
                return null;
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindNamed(root.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }

        static void RemoveReadout(Transform root)
        {
            if (root == null)
                return;
            Transform readout = FindNamed(root, "SpeedReadout");
            if (readout != null)
                UnityEngine.Object.Destroy(readout.gameObject);
        }

        static void StripClick(GameObject root)
        {
            foreach (Button button in root.GetComponentsInChildren<Button>(true))
                StripPersistent(button.onClick);
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
                else if (group != null)
                {
                    FieldInfo calls = group.GetType().GetField("m_Calls", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (calls?.GetValue(group) is System.Collections.IList list)
                        list.Clear();
                }
            }
            catch (Exception)
            {
            }
            evt.RemoveAllListeners();
        }
    }

    // The shade is moved out of the dialog, so hiding the dialog alone leaves it on screen.
    public class AutoSewGuard : MonoBehaviour
    {
        void OnDisable()
        {
            PauseMenuOptions.HideDimmer();
        }
    }

    // Shifts the pause-menu rows together so the list can move when it is taller than the screen.
    public class PauseListScroll : MonoBehaviour, IScrollHandler, IBeginDragHandler, IDragHandler
    {
        readonly List<RectTransform> _rows = new List<RectTransform>();
        readonly List<Vector2> _origins = new List<Vector2>();
        Scrollbar _bar;
        float _offset;
        float _limit;
        bool _applying;
        bool _captured;

        public static void Install(Transform list)
        {
            if (list == null || list.GetComponent<PauseListScroll>() != null || list.GetComponentInParent<ScrollRect>() != null)
                return;
            list.gameObject.AddComponent<PauseListScroll>();
        }

        void OnEnable()
        {
            if (!_captured)
                Capture();
        }

        void Capture()
        {
            _rows.Clear();
            _origins.Clear();
            float top = float.MinValue;
            float bottom = float.MaxValue;
            for (int i = 0; i < transform.childCount; i++)
            {
                var row = transform.GetChild(i) as RectTransform;
                if (!IsRow(row))
                    continue;
                _rows.Add(row);
                _origins.Add(row.anchoredPosition);
                float height = row.rect.height;
                if (height < 8f)
                    height = 40f;
                float rowTop = row.anchoredPosition.y + height * (1f - row.pivot.y);
                float rowBottom = rowTop - height;
                top = Mathf.Max(top, rowTop);
                bottom = Mathf.Min(bottom, rowBottom);
            }
            if (_rows.Count == 0)
                return;

            float span = top - bottom;
            var canvas = GetComponentInParent<Canvas>();
            var canvasRect = canvas != null ? canvas.transform as RectTransform : null;
            var parent = transform as RectTransform;
            float view = parent != null ? parent.rect.height : span;
            if (canvasRect != null)
                view = Mathf.Min(view, canvasRect.rect.height * 0.9f);
            if (view < 40f)
                view = span;
            _limit = Mathf.Max(0f, span - view + 12f);
            _captured = true;
            if (_bar == null && _limit > 1f)
                _bar = CreateBar();
            if (_bar != null)
            {
                _bar.gameObject.SetActive(_limit > 1f);
                _bar.size = span > 1f ? Mathf.Clamp01(view / span) : 1f;
                _bar.onValueChanged.AddListener(OnBar);
            }
            Apply();
            Plugin.Log.LogInfo("Pause menu scrolling is on.");
        }

        bool IsRow(RectTransform row)
        {
            if (row == null)
                return false;
            string name = row.name;
            if (name == "AutoSewPanel" || name == "AutoSewPauseBar" || name == "AutoSewScrollCatcher")
                return false;
            if (row.rect.height > 130f)
                return false;
            if (row.GetComponentInChildren<Toggle>(true) != null)
                return true;
            if (row.GetComponentInChildren<Slider>(true) != null)
                return true;
            return row.GetComponentInChildren<Button>(true) != null && row.rect.width > 80f;
        }

        Scrollbar CreateBar()
        {
            var root = new GameObject("AutoSewPauseBar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            root.transform.SetParent(transform, false);
            root.transform.SetAsLastSibling();
            var rect = root.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(12f, -16f);
            rect.anchoredPosition = new Vector2(-8f, 0f);
            root.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.18f);

            var handleGo = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handleGo.transform.SetParent(root.transform, false);
            var handle = handleGo.GetComponent<RectTransform>();
            handle.anchorMin = new Vector2(0f, 0f);
            handle.anchorMax = new Vector2(1f, 1f);
            handle.offsetMin = new Vector2(2f, 2f);
            handle.offsetMax = new Vector2(-2f, -2f);
            var handleImage = handleGo.GetComponent<Image>();
            handleImage.color = new Color(0.42f, 0.26f, 0.16f, 0.95f);

            var bar = root.GetComponent<Scrollbar>();
            bar.handleRect = handle;
            bar.targetGraphic = handleImage;
            bar.direction = Scrollbar.Direction.BottomToTop;
            return bar;
        }

        public void OnScroll(PointerEventData eventData)
        {
            Move(-eventData.scrollDelta.y * 42f);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
        }

        public void OnDrag(PointerEventData eventData)
        {
            float scale = 1f;
            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null && canvas.scaleFactor > 0.01f)
                scale = canvas.scaleFactor;
            Move(eventData.delta.y / scale);
        }

        void OnBar(float value)
        {
            if (_applying)
                return;
            _offset = (1f - value) * _limit;
            Apply();
        }

        void Move(float delta)
        {
            if (_limit <= 1f)
                return;
            _offset = Mathf.Clamp(_offset + delta, 0f, _limit);
            Apply();
        }

        void Apply()
        {
            _applying = true;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i] != null)
                    _rows[i].anchoredPosition = _origins[i] + new Vector2(0f, _offset);
            }
            if (_bar != null)
                _bar.SetValueWithoutNotify(_limit <= 1f ? 1f : 1f - _offset / _limit);
            _applying = false;
        }
    }
}
