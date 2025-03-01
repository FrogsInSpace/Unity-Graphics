﻿using System;
using System.Collections.Generic;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.UIElements;

namespace UnityEditor.Rendering
{
    public partial class RenderGraphViewer
    {
        static readonly string[] k_PassTypeNames =
        {
            "Legacy Render Pass",
            "Unsafe Render Pass",
            "Raster Render Pass",
            "Compute Pass"
        };

        static partial class Names
        {
            public const string kResourceListFoldout = "panel-resource-list";
            public const string kPassListFoldout = "panel-pass-list";
            public const string kResourceSearchField = "resource-search-field";
            public const string kPassSearchField = "pass-search-field";
        }
        static partial class Classes
        {
            public const string kPanelListLineBreak = "panel-list__line-break";
            public const string kPanelListItem = "panel-list__item";
            public const string kPanelListItemSelectionAnimation = "panel-list__item--selection-animation";
            public const string kPanelResourceListItem = "panel-resource-list__item";
            public const string kPanelPassListItem = "panel-pass-list__item";
            public const string kSubHeaderText = "sub-header-text";
            public const string kAttachmentInfoItem = "attachment-info__item";
            public const string kCustomFoldoutArrow = "custom-foldout-arrow";
        }

        static readonly System.Text.RegularExpressions.Regex k_TagRegex = new ("<[^>]*>");
        const string k_SelectionColorBeginTag = "<mark=#3169ACAB>";
        const string k_SelectionColorEndTag = "</mark>";

        bool m_ResourceListExpanded = true;
        bool m_PassListExpanded = true;

        Dictionary<VisualElement, List<TextElement>> m_ResourceDescendantCache = new ();
        Dictionary<VisualElement, List<TextElement>> m_PassDescendantCache = new ();

        void InitializeSidePanel()
        {
            // Callbacks for dynamic height allocation between resource & pass lists
            HeaderFoldout resourceListFoldout = rootVisualElement.Q<HeaderFoldout>(Names.kResourceListFoldout);
            resourceListFoldout.RegisterValueChangedCallback(evt =>
            {
                m_ResourceListExpanded = resourceListFoldout.value;
                UpdatePanelHeights();
            });
            resourceListFoldout.icon = m_ResourceListIcon;
            resourceListFoldout.contextMenuGenerator = () => CreateContextMenu(resourceListFoldout.Q<ScrollView>());

            HeaderFoldout passListFoldout = rootVisualElement.Q<HeaderFoldout>(Names.kPassListFoldout);
            passListFoldout.RegisterValueChangedCallback(evt =>
            {
                m_PassListExpanded = passListFoldout.value;
                UpdatePanelHeights();
            });
            passListFoldout.icon = m_PassListIcon;
            passListFoldout.contextMenuGenerator = () => CreateContextMenu(passListFoldout.Q<ScrollView>());

            // Search fields
            var resourceSearchField = rootVisualElement.Q<ToolbarSearchField>(Names.kResourceSearchField);
            resourceSearchField.placeholderText = "Search";
            resourceSearchField.RegisterValueChangedCallback(evt => OnSearchFilterChanged(m_ResourceDescendantCache, evt.newValue));

            var passSearchField = rootVisualElement.Q<ToolbarSearchField>(Names.kPassSearchField);
            passSearchField.placeholderText = "Search";
            passSearchField.RegisterValueChangedCallback(evt => OnSearchFilterChanged(m_PassDescendantCache, evt.newValue));
        }

        bool IsSearchFilterMatch(string str, string searchString, out int startIndex, out int endIndex)
        {
            startIndex = -1;
            endIndex = -1;

            startIndex = str.IndexOf(searchString, 0, StringComparison.CurrentCultureIgnoreCase);
            if (startIndex == -1)
                return false;

            endIndex = startIndex + searchString.Length - 1;
            return true;
        }

        void OnSearchFilterChanged(Dictionary<VisualElement, List<TextElement>> elementCache, string searchString)
        {
            // Display filter
            foreach (var (foldout, descendants) in elementCache)
            {
                bool anyDescendantMatchesSearch = false;
                foreach (var elem in descendants)
                {
                    // Remove any existing highlight
                    var text = elem.text;
                    var hasHighlight = k_TagRegex.IsMatch(text);
                    text = k_TagRegex.Replace(text, string.Empty);
                    if (!IsSearchFilterMatch(text, searchString, out int startHighlight, out int endHighlight))
                    {
                        if (hasHighlight)
                            elem.text = text;
                        continue;
                    }


                    text = text.Insert(startHighlight, k_SelectionColorBeginTag);
                    text = text.Insert(endHighlight + k_SelectionColorBeginTag.Length + 1, k_SelectionColorEndTag);
                    elem.text = text;
                    anyDescendantMatchesSearch = true;
                }
                foldout.style.display = anyDescendantMatchesSearch ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        void SetChildFoldoutsExpanded(VisualElement elem, bool expanded)
        {
            elem.Query<Foldout>().ForEach(f => f.value = expanded);
        }

        GenericMenu CreateContextMenu(VisualElement content)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Collapse All"), false, () => SetChildFoldoutsExpanded(content, false));
            menu.AddItem(new GUIContent("Expand All"), false, () => SetChildFoldoutsExpanded(content, true));
            return menu;
        }

        void PopulateResourceList()
        {
            ScrollView content = rootVisualElement.Q<HeaderFoldout>(Names.kResourceListFoldout).Q<ScrollView>();
            content.Clear();

            UpdatePanelHeights();

            m_ResourceDescendantCache.Clear();

            int visibleResourceIndex = 0;
            foreach (var visibleResourceElement in m_ResourceElementsInfo)
            {
                var resourceData = m_CurrentDebugData.resourceLists[(int)visibleResourceElement.type][visibleResourceElement.index];

                var resourceItem = new Foldout();
                resourceItem.text = resourceData.name;
                resourceItem.value = false;
                resourceItem.userData = visibleResourceIndex;
                resourceItem.AddToClassList(Classes.kPanelListItem);
                resourceItem.AddToClassList(Classes.kPanelResourceListItem);
                resourceItem.AddToClassList(Classes.kCustomFoldoutArrow);
                visibleResourceIndex++;

                var iconContainer = new VisualElement();
                iconContainer.AddToClassList(Classes.kResourceIconContainer);

                var importedIcon = new VisualElement();
                importedIcon.AddToClassList(Classes.kResourceIconImported);
                importedIcon.tooltip = "Imported resource";
                importedIcon.style.display = resourceData.imported ? DisplayStyle.Flex : DisplayStyle.None;
                iconContainer.Add(importedIcon);

                var foldoutCheckmark = resourceItem.Q("unity-checkmark");
                // Add resource type icon before the label
                foldoutCheckmark.parent.Insert(1, CreateResourceTypeIcon(visibleResourceElement.type));
                foldoutCheckmark.BringToFront(); // Move foldout checkmark to the right

                // Add imported icon to the right of the foldout checkmark
                var toggleContainer = resourceItem.Q<Toggle>();
                toggleContainer.tooltip = resourceData.name;
                toggleContainer.Add(iconContainer);
                RenderGraphResourceType type = (RenderGraphResourceType)visibleResourceElement.type;
                if (type == RenderGraphResourceType.Texture && resourceData.textureData != null)
                {
                    var lineBreak = new VisualElement();
                    lineBreak.AddToClassList(Classes.kPanelListLineBreak);
                    resourceItem.Add(lineBreak);
                    resourceItem.Add(new Label($"Size: {resourceData.textureData.width}x{resourceData.textureData.height}x{resourceData.textureData.depth}"));
                    resourceItem.Add(new Label($"Format: {resourceData.textureData.format.ToString()}"));
                    resourceItem.Add(new Label($"Clear: {resourceData.textureData.clearBuffer}"));
                    resourceItem.Add(new Label($"BindMS: {resourceData.textureData.bindMS}"));
                    resourceItem.Add(new Label($"Samples: {resourceData.textureData.samples}"));
                    if (m_CurrentDebugData.isNRPCompiler)
                        resourceItem.Add(new Label($"Memoryless: {resourceData.memoryless}"));
                }
                else if (type == RenderGraphResourceType.Buffer && resourceData.bufferData != null)
                {
                    var lineBreak = new VisualElement();
                    lineBreak.AddToClassList(Classes.kPanelListLineBreak);
                    resourceItem.Add(lineBreak);
                    resourceItem.Add(new Label($"Count: {resourceData.bufferData.count}"));
                    resourceItem.Add(new Label($"Stride: {resourceData.bufferData.stride}"));
                    resourceItem.Add(new Label($"Target: {resourceData.bufferData.target.ToString()}"));
                    resourceItem.Add(new Label($"Usage: {resourceData.bufferData.usage.ToString()}"));
                }

                content.Add(resourceItem);

                m_ResourceDescendantCache[resourceItem] = resourceItem.Query().Descendents<TextElement>().ToList();
            }
        }

        void PopulatePassList()
        {
            HeaderFoldout headerFoldout = rootVisualElement.Q<HeaderFoldout>(Names.kPassListFoldout);
            if (!m_CurrentDebugData.isNRPCompiler)
            {
                headerFoldout.style.display = DisplayStyle.None;
                return;
            }
            headerFoldout.style.display = DisplayStyle.Flex;

            ScrollView content = headerFoldout.Q<ScrollView>();
            content.Clear();

            UpdatePanelHeights();

            m_PassDescendantCache.Clear();

            void CreateTextElement(VisualElement parent, string text, string className = null)
            {
                var textElement = new TextElement();
                textElement.text = text;
                if (className != null)
                    textElement.AddToClassList(className);
                parent.Add(textElement);
            }

            HashSet<int> addedPasses = new HashSet<int>();

            foreach (var visiblePassElement in m_PassElementsInfo)
            {
                if (addedPasses.Contains(visiblePassElement.passId))
                    continue; // Add only one item per merged pass group

                List<RenderGraph.DebugData.PassData> passDatas = new();
                List<string> passNames = new();
                var groupedPassIds = GetGroupedPassIds(visiblePassElement.passId);
                foreach (int groupedId in groupedPassIds) {
                    addedPasses.Add(groupedId);
                    passDatas.Add(m_CurrentDebugData.passList[groupedId]);
                    passNames.Add(m_CurrentDebugData.passList[groupedId].name);
                }

                var passItem = new Foldout();
                passItem.text = string.Join(", ", passNames);
                passItem.Q<Toggle>().tooltip = passItem.text;
                passItem.value = false;
                passItem.userData = m_PassIdToVisiblePassIndex[visiblePassElement.passId];
                passItem.AddToClassList(Classes.kPanelListItem);
                passItem.AddToClassList(Classes.kPanelPassListItem);

                //Native pass info (duplicated for each pass group so just look at the first)
                var firstPassData = passDatas[0];
                var nativePassInfo = firstPassData.nrpInfo?.nativePassInfo;

                if (nativePassInfo != null)
                {
                    if (nativePassInfo.mergedPassIds.Count == 1)
                        CreateTextElement(passItem, "Native Pass was created from Raster Render Pass.");
                    else if (nativePassInfo.mergedPassIds.Count > 1)
                        CreateTextElement(passItem, $"Native Pass was created by merging {nativePassInfo.mergedPassIds.Count} Raster Render Passes.");

                    CreateTextElement(passItem, "Pass break reasoning", Classes.kSubHeaderText);
                    CreateTextElement(passItem, nativePassInfo.passBreakReasoning);
                }
                else
                {
                    var msg = $"This is a {k_PassTypeNames[(int) firstPassData.type]}. Only Raster Render Passes can be merged.";
                    msg = msg.Replace("a Unsafe", "an Unsafe");
                    CreateTextElement(passItem, msg);
                }

                CreateTextElement(passItem, "Render Graph Pass Info", Classes.kSubHeaderText);
                foreach (int passId in groupedPassIds)
                {
                    var pass = m_CurrentDebugData.passList[passId];
                    Debug.Assert(pass.nrpInfo != null); // This overlay currently assumes NRP compiler
                    var passFoldout = new Foldout();
                    passFoldout.text = $"{pass.name} ({k_PassTypeNames[(int) pass.type]})";
                    passFoldout.AddToClassList(Classes.kAttachmentInfoItem);
                    passFoldout.AddToClassList(Classes.kCustomFoldoutArrow);
                    passFoldout.Q<Toggle>().tooltip = passFoldout.text;

                    var foldoutCheckmark = passFoldout.Q("unity-checkmark");
                    foldoutCheckmark.BringToFront(); // Move foldout checkmark to the right

                    var lineBreak = new VisualElement();
                    lineBreak.AddToClassList(Classes.kPanelListLineBreak);
                    passFoldout.Add(lineBreak);

                    CreateTextElement(passFoldout,
                        $"Attachment dimensions: {pass.nrpInfo.width}x{pass.nrpInfo.height}x{pass.nrpInfo.volumeDepth}");
                    CreateTextElement(passFoldout, $"Has depth attachment: {pass.nrpInfo.hasDepth}");
                    CreateTextElement(passFoldout, $"MSAA samples: {pass.nrpInfo.samples}");
                    CreateTextElement(passFoldout, $"Async compute: {pass.async}");

                    passItem.Add(passFoldout);
                }

                CreateTextElement(passItem, "Attachment Load/Store Actions", Classes.kSubHeaderText);
                if (nativePassInfo != null && nativePassInfo.attachmentInfos.Count > 0)
                {
                    foreach (var attachmentInfo in nativePassInfo.attachmentInfos)
                    {
                        var attachmentFoldout = new Foldout();
                        attachmentFoldout.text = attachmentInfo.resourceName;
                        attachmentFoldout.AddToClassList(Classes.kAttachmentInfoItem);
                        attachmentFoldout.AddToClassList(Classes.kCustomFoldoutArrow);
                        attachmentFoldout.Q<Toggle>().tooltip = attachmentFoldout.text;

                        var foldoutCheckmark = attachmentFoldout.Q("unity-checkmark");
                        foldoutCheckmark.BringToFront(); // Move foldout checkmark to the right

                        var lineBreak = new VisualElement();
                        lineBreak.AddToClassList(Classes.kPanelListLineBreak);
                        attachmentFoldout.Add(lineBreak);

                        attachmentFoldout.Add(new TextElement
                        {
                            text = $"<b>Load action:</b> {attachmentInfo.loadAction} ({attachmentInfo.loadReason})"
                        });
                        attachmentFoldout.Add(new TextElement
                        {
                            text = $"<b>Store action:</b> {attachmentInfo.storeAction} ({attachmentInfo.storeReason})"
                        });

                        passItem.Add(attachmentFoldout);
                    }
                }
                else
                {
                    CreateTextElement(passItem, "No attachments.");
                }

                content.Add(passItem);

                m_PassDescendantCache[passItem] = passItem.Query().Descendents<TextElement>().ToList();
            }
        }

        void UpdatePanelHeights()
        {
            HeaderFoldout resourceListFoldout = rootVisualElement.Q<HeaderFoldout>(Names.kResourceListFoldout);
            HeaderFoldout passListFoldout = rootVisualElement.Q<HeaderFoldout>(Names.kPassListFoldout);

            bool passListExpanded = m_PassListExpanded && (m_CurrentDebugData != null && m_CurrentDebugData.isNRPCompiler);
            const int kFoldoutHeaderHeightPx = 18;
            if (m_ResourceListExpanded && !passListExpanded)
            {
                resourceListFoldout.style.maxHeight = Length.Percent(100);
                passListFoldout.style.maxHeight = kFoldoutHeaderHeightPx;
            }
            else if (!m_ResourceListExpanded && passListExpanded)
            {
                resourceListFoldout.style.maxHeight = kFoldoutHeaderHeightPx;
                passListFoldout.style.maxHeight = Length.Percent(100);
            }
            else if (m_ResourceListExpanded && passListExpanded)
            {
                resourceListFoldout.style.maxHeight = Length.Percent(50);
                passListFoldout.style.maxHeight = Length.Percent(50);
            }
            else
            {
                resourceListFoldout.style.maxHeight = kFoldoutHeaderHeightPx;
                passListFoldout.style.maxHeight = kFoldoutHeaderHeightPx;
            }
        }

        void ScrollToPass(int visiblePassIndex)
        {
            var passFoldout = rootVisualElement.Q<HeaderFoldout>(Names.kPassListFoldout);
            ScrollToFoldout(passFoldout, visiblePassIndex);
        }

        void ScrollToResource(int visibleResourceIndex)
        {
            var resourceFoldout = rootVisualElement.Q<HeaderFoldout>(Names.kResourceListFoldout);
            ScrollToFoldout(resourceFoldout, visibleResourceIndex);
        }

        void ScrollToFoldout(VisualElement parent, int index)
        {
            ScrollView scrollView = parent.Q<ScrollView>();
            scrollView.Query<Foldout>(classes: Classes.kPanelListItem).ForEach(foldout =>
            {
                if (index == (int)foldout.userData)
                {
                    // Trigger animation
                    foldout.AddToClassList(Classes.kPanelListItemSelectionAnimation);
                    foldout.RegisterCallbackOnce<TransitionEndEvent>(_ =>
                        foldout.RemoveFromClassList(Classes.kPanelListItemSelectionAnimation));

                    // Open foldout
                    foldout.value = true;
                    // Defer scrolling to allow foldout to be expanded first
                    scrollView.schedule.Execute(() => scrollView.ScrollTo(foldout)).StartingIn(50);
                }
            });
        }
    }
}
