#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MeshLODStudio.Editor
{
    /// <summary>
    /// Commercial-grade UI Toolkit EditorWindow for MeshLOD Studio.
    /// Default workspace is Prefab Studio (Prefab-First workflow).
    /// Open via Tools > MeshLOD Studio > Open Studio.
    /// </summary>
    public sealed class MeshLODStudioWindow : EditorWindow
    {
        [SerializeField] private WorkspaceTab currentTab = WorkspaceTab.PrefabStudio;
        [SerializeField] private GameObject activePrefabAsset;
        [SerializeField] private int selectedNodeIndex = 0;
        [SerializeField] private int selectedPresetIndex = 1;

        private MeshLODStudioSettings settings;
        private PrefabScanReport currentReport;
        private SceneAutoSetupService.SceneScanSummary currentSceneScan;
        private readonly List<PrefabScanReport> batchReports = new List<PrefabScanReport>();
        private MeshLODPreviewController previewController;

        private VisualElement navSidebar;
        private ScrollView workspaceScroll;
        private Label statusMessageLabel;
        private readonly Dictionary<WorkspaceTab, Button> navButtons = new Dictionary<WorkspaceTab, Button>();

        private static readonly Color[] LodBarColors = new Color[]
        {
            new Color(0.18f, 0.55f, 0.34f),
            new Color(0.20f, 0.45f, 0.78f),
            new Color(0.68f, 0.48f, 0.14f),
            new Color(0.58f, 0.25f, 0.68f),
            new Color(0.45f, 0.45f, 0.50f)
        };

        [MenuItem("Tools/MeshLOD Studio/Open Studio %#l", false, 10)]
        public static void OpenStudio()
        {
            var window = GetWindow<MeshLODStudioWindow>("MeshLOD Studio");
            window.minSize = new Vector2(960f, 620f);
            window.Show();
        }

        private void OnEnable()
        {
            settings = MeshLODStudioSettings.GetOrCreate();
            previewController = new MeshLODPreviewController();
            Selection.selectionChanged += OnUnitySelectionChanged;
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= OnUnitySelectionChanged;
            if (previewController != null)
            {
                previewController.Dispose();
                previewController = null;
            }
        }

        public void CreateGUI()
        {
            settings = MeshLODStudioSettings.GetOrCreate();
            VisualElement root = rootVisualElement;
            root.Clear();

            StyleSheet uss = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/Editor/MeshLODStudio/UI/MeshLODStudioStyles.uss");
            if (uss != null) root.styleSheets.Add(uss);

            VisualTreeAsset uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Editor/MeshLODStudio/UI/MeshLODStudioWindow.uxml");
            if (uxml != null)
            {
                uxml.CloneTree(root);
                navSidebar = root.Q<VisualElement>("nav-sidebar");
                workspaceScroll = root.Q<ScrollView>("workspace-scroll");
                statusMessageLabel = root.Q<Label>("status-message");
            }
            else
            {
                BuildFallbackRootLayout(root);
            }

            BuildHeaderBar(root.Q<VisualElement>("header-actions"));
            BuildNavigationSidebar();
            DetectSelectedPrefabFromUnitySelection(false);
            RefreshActiveWorkspace();
        }

        private void BuildFallbackRootLayout(VisualElement root)
        {
            root.style.flexGrow = 1;
            root.style.backgroundColor = new Color(0.09f, 0.10f, 0.11f);

            var topHeader = new VisualElement { name = "top-header" };
            topHeader.style.height = 44;
            topHeader.style.flexDirection = FlexDirection.Row;
            topHeader.style.alignItems = Align.Center;
            topHeader.style.justifyContent = Justify.SpaceBetween;
            topHeader.style.paddingLeft = 16;
            topHeader.style.paddingRight = 16;
            topHeader.style.backgroundColor = new Color(0.07f, 0.075f, 0.09f);

            var titleBox = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            titleBox.Add(new Label("MeshLOD Studio") { style = { fontSize = 15, unityFontStyleAndWeight = FontStyle.Bold } });
            topHeader.Add(titleBox);

            var headerActions = new VisualElement { name = "header-actions", style = { flexDirection = FlexDirection.Row } };
            topHeader.Add(headerActions);
            root.Add(topHeader);

            var body = new VisualElement { style = { flexGrow = 1, flexDirection = FlexDirection.Row } };
            navSidebar = new VisualElement { name = "nav-sidebar", style = { width = 205, paddingLeft = 8, paddingRight = 8, paddingTop = 10, backgroundColor = new Color(0.08f, 0.085f, 0.10f) } };
            workspaceScroll = new ScrollView { name = "workspace-scroll", style = { flexGrow = 1, paddingLeft = 16, paddingRight = 16, paddingTop = 16, paddingBottom = 16 } };
            body.Add(navSidebar);
            body.Add(workspaceScroll);
            root.Add(body);

            var statusBar = new VisualElement { style = { height = 28, flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingLeft = 14, paddingRight = 14, backgroundColor = new Color(0.07f, 0.075f, 0.09f) } };
            statusMessageLabel = new Label("Ready.");
            statusBar.Add(statusMessageLabel);
            root.Add(statusBar);
        }

        private void BuildHeaderBar(VisualElement headerActions)
        {
            if (headerActions == null) return;
            headerActions.Clear();

            var prefabField = new ObjectField
            {
                objectType = typeof(GameObject),
                allowSceneObjects = false,
                value = activePrefabAsset,
                style = { width = 240, marginRight = 8 }
            };
            prefabField.RegisterValueChangedCallback(evt =>
            {
                activePrefabAsset = evt.newValue as GameObject;
                ScanCurrentPrefab();
                RefreshActiveWorkspace();
            });
            headerActions.Add(prefabField);

            var scanBtn = new Button(() =>
            {
                ScanCurrentPrefab();
                RefreshActiveWorkspace();
            })
            {
                text = "Rescan Prefab"
            };
            scanBtn.AddToClassList("secondary-button");
            headerActions.Add(scanBtn);
        }

        private void BuildNavigationSidebar()
        {
            navSidebar.Clear();
            navButtons.Clear();

            AddNavSection("PREFAB-FIRST WORKFLOW");
            AddNavButton(WorkspaceTab.Dashboard, "1. Dashboard");
            AddNavButton(WorkspaceTab.PrefabStudio, "2. Prefab Studio (Default)");
            AddNavButton(WorkspaceTab.MeshLODConverter, "3. Mesh LOD Converter");
            AddNavButton(WorkspaceTab.LODGroupEditor, "4. LOD Group Editor");

            AddNavSection("SCALING & SCENE TOOLS");
            AddNavButton(WorkspaceTab.BatchOptimizer, "5. Batch Optimizer");
            AddNavButton(WorkspaceTab.SceneAutoSetup, "6. Scene Auto Setup");

            AddNavSection("CONFIGURATION");
            AddNavButton(WorkspaceTab.PresetManager, "7. Preset Manager");
            AddNavButton(WorkspaceTab.Settings, "8. Settings & Validation");

            UpdateNavSelectionVisuals();
        }

        private void AddNavSection(string title)
        {
            var label = new Label(title);
            label.AddToClassList("nav-section-label");
            navSidebar.Add(label);
        }

        private void AddNavButton(WorkspaceTab tab, string text)
        {
            var btn = new Button(() => SwitchTab(tab)) { text = text };
            btn.AddToClassList("nav-button");
            navButtons[tab] = btn;
            navSidebar.Add(btn);
        }

        private void SwitchTab(WorkspaceTab tab)
        {
            currentTab = tab;
            UpdateNavSelectionVisuals();
            RefreshActiveWorkspace();
        }

        private void UpdateNavSelectionVisuals()
        {
            foreach (var kvp in navButtons)
            {
                if (kvp.Key == currentTab)
                    kvp.Value.AddToClassList("nav-button-active");
                else
                    kvp.Value.RemoveFromClassList("nav-button-active");
            }
        }

        private void OnUnitySelectionChanged()
        {
            DetectSelectedPrefabFromUnitySelection(true);
        }

        private void DetectSelectedPrefabFromUnitySelection(bool autoRefreshUI)
        {
            GameObject selectedGo = Selection.activeObject as GameObject;
            if (selectedGo != null && EditorUtility.IsPersistent(selectedGo))
            {
                string path = AssetDatabase.GetAssetPath(selectedGo);
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    if (activePrefabAsset != selectedGo)
                    {
                        activePrefabAsset = selectedGo;
                        ScanCurrentPrefab();
                        if (autoRefreshUI) RefreshActiveWorkspace();
                    }
                }
            }
        }

        private void ScanCurrentPrefab()
        {
            if (activePrefabAsset == null)
            {
                currentReport = null;
                SetStatus("No Prefab asset selected. Select a .prefab in the Project window.");
                return;
            }

            currentReport = PrefabScannerAndValidator.ScanPrefabAsset(activePrefabAsset, settings);
            selectedNodeIndex = 0;

            if (currentReport != null)
            {
                SetStatus($"Scan completed: '{currentReport.prefabName}' — {currentReport.compatibleNodeCount} convertible object(s), {currentReport.maxDetectedLodLevels} max Mesh LOD levels.");
                settings.RecordOperation("Scan Prefab", currentReport.prefabName, true, $"Found {currentReport.compatibleNodeCount} compatible mesh node(s).");
            }
        }

        private void SetStatus(string message)
        {
            if (statusMessageLabel != null)
                statusMessageLabel.text = message;
        }

        private void RefreshActiveWorkspace()
        {
            if (workspaceScroll == null) return;
            workspaceScroll.Clear();

            VisualElement headerActions = rootVisualElement.Q<VisualElement>("header-actions");
            BuildHeaderBar(headerActions);

            switch (currentTab)
            {
                case WorkspaceTab.Dashboard:
                    BuildDashboardView();
                    break;
                case WorkspaceTab.PrefabStudio:
                    BuildPrefabStudioView();
                    break;
                case WorkspaceTab.MeshLODConverter:
                    BuildMeshLODConverterView();
                    break;
                case WorkspaceTab.LODGroupEditor:
                    BuildLODGroupEditorView();
                    break;
                case WorkspaceTab.BatchOptimizer:
                    BuildBatchOptimizerView();
                    break;
                case WorkspaceTab.SceneAutoSetup:
                    BuildSceneAutoSetupView();
                    break;
                case WorkspaceTab.PresetManager:
                    BuildPresetManagerView();
                    break;
                case WorkspaceTab.Settings:
                    BuildSettingsView();
                    break;
            }
        }

        // ====================================================================
        // 1. DASHBOARD WORKSPACE
        // ====================================================================
        private void BuildDashboardView()
        {
            var introCard = CreateCard("Dashboard Overview");
            introCard.Add(new Label("MeshLOD Studio converts Unity 6.3 generated Mesh LODs (stored in shared mesh index buffers) into independent Unity LODGroups on your source Prefab assets. All normal scene instances inherit the changes automatically."));
            workspaceScroll.Add(introCard);

            if (currentReport == null)
            {
                var emptyCard = CreateCard("No Prefab Asset Selected");
                emptyCard.Add(new Label("Select any Prefab asset (.prefab) in your Project window or assign one in the top toolbar to inspect its Mesh LOD levels."));
                workspaceScroll.Add(emptyCard);
            }
            else
            {
                var metricsCard = CreateCard($"Active Prefab: {currentReport.prefabName} ({currentReport.assetPath})");
                var metricRow = new VisualElement();
                metricRow.AddToClassList("metric-row");

                metricRow.Add(CreateMetricBox(currentReport.maxDetectedLodLevels.ToString(), "Max Mesh LOD Levels"));
                metricRow.Add(CreateMetricBox(currentReport.compatibleNodeCount.ToString(), "Convertible Objects"));
                metricRow.Add(CreateMetricBox(currentReport.existingLODGroupCount.ToString(), "Existing LODGroups"));
                metricRow.Add(CreateMetricBox(currentReport.unsupportedNodeCount.ToString(), "Unsupported / Skipped"));
                metricsCard.Add(metricRow);

                var quickActions = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 6 } };
                var openStudioBtn = new Button(() => SwitchTab(WorkspaceTab.PrefabStudio)) { text = "Inspect in Prefab Studio" };
                openStudioBtn.AddToClassList("primary-button");
                openStudioBtn.style.marginRight = 8;
                quickActions.Add(openStudioBtn);

                var openConverterBtn = new Button(() => SwitchTab(WorkspaceTab.MeshLODConverter)) { text = "Open Mesh LOD Converter" };
                openConverterBtn.AddToClassList("secondary-button");
                quickActions.Add(openConverterBtn);

                if (currentReport.hasBackupRecord)
                {
                    var rollbackBtn = new Button(ExecuteRollbackOnCurrentPrefab) { text = "Restore Original Prefab Backup" };
                    rollbackBtn.AddToClassList("danger-button");
                    rollbackBtn.style.marginLeft = 8;
                    quickActions.Add(rollbackBtn);
                }

                metricsCard.Add(quickActions);
                workspaceScroll.Add(metricsCard);
            }

            var logCard = CreateCard("Recent Operations");
            if (settings.recentOperations.Count == 0)
            {
                logCard.Add(new Label("No operations recorded yet."));
            }
            else
            {
                int count = Mathf.Min(12, settings.recentOperations.Count);
                for (int i = 0; i < count; i++)
                {
                    OperationLogEntry log = settings.recentOperations[i];
                    var row = new VisualElement();
                    row.AddToClassList("table-row");
                    row.Add(CreateBadge(log.succeeded ? "SUCCESS" : "FAILED", log.succeeded ? "badge-ok" : "badge-error"));
                    row.Add(new Label($"[{log.timestamp}] {log.operationType} — {log.targetName}: {log.summary}") { style = { marginLeft = 8 } });
                    logCard.Add(row);
                }
            }
            workspaceScroll.Add(logCard);
        }

        // ====================================================================
        // 2. PREFAB STUDIO WORKSPACE (DEFAULT)
        // ====================================================================
        private void BuildPrefabStudioView()
        {
            if (currentReport == null)
            {
                var card = CreateCard("Prefab Studio — Select a Prefab Asset");
                card.Add(new Label("Select a Prefab asset in the Project window (e.g., Assets/Environment/Trees/Oak_Tree.prefab) to analyze its hierarchy and generated Mesh LOD data."));
                workspaceScroll.Add(card);
                return;
            }

            var headerCard = CreateCard($"Prefab Hierarchy Analysis: {currentReport.prefabName}");
            headerCard.Add(new Label($"GUID: {currentReport.assetGuid}  |  Type: {currentReport.prefabAssetType}  |  Total Objects: {currentReport.totalGameObjects}  |  Mesh Renderers: {currentReport.totalMeshRenderers}"));

            var batchBar = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 8 } };
            var selectAllBtn = new Button(() =>
            {
                foreach (var n in currentReport.meshNodes)
                    if (n.status == NodeCompatibilityStatus.Compatible) n.selectedForConversion = true;
                RefreshActiveWorkspace();
            }) { text = "Select All Compatible" };
            selectAllBtn.AddToClassList("secondary-button");
            selectAllBtn.style.marginRight = 6;

            var deselectAllBtn = new Button(() =>
            {
                foreach (var n in currentReport.meshNodes)
                    n.selectedForConversion = false;
                RefreshActiveWorkspace();
            }) { text = "Deselect All" };
            deselectAllBtn.AddToClassList("secondary-button");
            deselectAllBtn.style.marginRight = 12;

            var goConvertBtn = new Button(() => SwitchTab(WorkspaceTab.MeshLODConverter)) { text = "Configure & Convert Selected Objects →" };
            goConvertBtn.AddToClassList("primary-button");

            batchBar.Add(selectAllBtn);
            batchBar.Add(deselectAllBtn);
            batchBar.Add(goConvertBtn);
            headerCard.Add(batchBar);
            workspaceScroll.Add(headerCard);

            var splitContainer = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            var treeCard = CreateCard("Mesh Objects in Prefab");
            treeCard.style.flexGrow = 1;
            treeCard.style.marginRight = 10;

            if (currentReport.meshNodes.Count == 0)
            {
                treeCard.Add(new Label("No MeshFilter/MeshRenderer components found in this Prefab."));
            }
            else
            {
                for (int i = 0; i < currentReport.meshNodes.Count; i++)
                {
                    int idx = i;
                    PrefabNodeScanResult node = currentReport.meshNodes[i];

                    var row = new VisualElement();
                    row.AddToClassList("table-row");
                    if (idx == selectedNodeIndex)
                        row.style.backgroundColor = new Color(0.18f, 0.24f, 0.36f, 0.65f);

                    var toggle = new Toggle { value = node.selectedForConversion };
                    toggle.SetEnabled(node.status == NodeCompatibilityStatus.Compatible);
                    toggle.RegisterValueChangedCallback(evt => node.selectedForConversion = evt.newValue);
                    row.Add(toggle);

                    var selectBtn = new Button(() =>
                    {
                        selectedNodeIndex = idx;
                        RefreshActiveWorkspace();
                    })
                    {
                        text = $"{new string(' ', node.depth * 2)}{node.gameObjectName} ({node.detectedMeshLodCount} LODs, {node.baseTriangleCount} tris)",
                        style = { flexGrow = 1, unityTextAlign = TextAnchor.MiddleLeft, backgroundColor = Color.clear, borderLeftWidth = 0, borderRightWidth = 0, borderTopWidth = 0, borderBottomWidth = 0 }
                    };
                    row.Add(selectBtn);

                    string badgeClass = node.status == NodeCompatibilityStatus.Compatible
                        ? "badge-ok"
                        : (node.status == NodeCompatibilityStatus.AlreadyConvertedByStudio || node.status == NodeCompatibilityStatus.HasExistingManualLODGroup ? "badge-warn" : "badge-error");
                    row.Add(CreateBadge(node.status.ToString(), badgeClass));

                    treeCard.Add(row);
                }
            }

            var detailCard = CreateCard("Selected Object Details & Interactive Preview");
            detailCard.style.width = 430;

            if (currentReport.meshNodes.Count > 0 && selectedNodeIndex >= 0 && selectedNodeIndex < currentReport.meshNodes.Count)
            {
                PrefabNodeScanResult selNode = currentReport.meshNodes[selectedNodeIndex];
                detailCard.Add(new Label($"Hierarchy Path: {selNode.hierarchyPath}") { style = { unityFontStyleAndWeight = FontStyle.Bold } });
                detailCard.Add(new Label($"Status: {selNode.statusMessage}") { style = { whiteSpace = WhiteSpace.Normal, marginBottom = 6 } });
                detailCard.Add(new Label($"Shared Mesh: {(selNode.sharedMesh != null ? selNode.sharedMesh.name : "None")}  |  SubMeshes: {selNode.subMeshCount}  |  Vertices: {selNode.vertexCount}"));
                detailCard.Add(new Label($"Materials ({selNode.sharedMaterials.Length}): {FormatMaterialNames(selNode.sharedMaterials)}"));

                if (selNode.otherComponentTypes.Length > 0)
                {
                    detailCard.Add(new Label($"Preserved Components: {string.Join(", ", selNode.otherComponentTypes)}"));
                }

                if (selNode.status == NodeCompatibilityStatus.MeshLODNotEnabledOnImporter && selNode.sharedMesh != null)
                {
                    var enableImporterBtn = new Button(() =>
                    {
                        if (MeshLODDetectorAndExtractor.EnableMeshLodOnModelImporter(selNode.sharedMesh, out string msg))
                        {
                            ScanCurrentPrefab();
                            SetStatus(msg);
                            RefreshActiveWorkspace();
                        }
                        else
                        {
                            SetStatus(msg);
                        }
                    })
                    {
                        text = "Enable 'Generate Mesh LODs' on ModelImporter & Reimport"
                    };
                    enableImporterBtn.AddToClassList("primary-button");
                    enableImporterBtn.style.marginTop = 8;
                    detailCard.Add(enableImporterBtn);
                }

                if (selNode.lodLevels.Count > 0)
                {
                    detailCard.Add(new Label("Detected Mesh LOD Index Buffer Levels:") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 8 } });
                    foreach (LodLevelMetadata meta in selNode.lodLevels)
                    {
                        detailCard.Add(new Label($"• LOD{meta.lodIndex}: {meta.totalTriangleCount} tris ({meta.totalIndexCount} indices, ~{meta.estimatedUniqueVertices} verts) [-{meta.reductionPercentFromLod0:0.0}%]"));
                    }
                }

                if (selNode.sharedMesh != null)
                {
                    BuildEmbeddedPreviewElement(detailCard, selNode.sharedMesh, selNode.sharedMaterials);
                }
            }

            splitContainer.Add(treeCard);
            splitContainer.Add(detailCard);
            workspaceScroll.Add(splitContainer);
        }

        // ====================================================================
        // 3. MESH LOD CONVERTER WORKSPACE
        // ====================================================================
        private void BuildMeshLODConverterView()
        {
            if (currentReport == null)
            {
                var card = CreateCard("Mesh LOD Converter — No Prefab Selected");
                card.Add(new Label("Select a Prefab asset in the Project window first."));
                workspaceScroll.Add(card);
                return;
            }

            var configCard = CreateCard($"Convert Mesh LODs to LODGroup: {currentReport.prefabName}");
            MeshLODPreset activePreset = GetSelectedPreset();

            var presetChoices = new List<string>();
            foreach (var p in settings.presets) presetChoices.Add(p.presetName);

            var presetDropdown = new PopupField<string>("Optimization Preset", presetChoices, activePreset.presetName);
            presetDropdown.RegisterValueChangedCallback(evt =>
            {
                currentReport.selectedPresetName = evt.newValue;
                selectedPresetIndex = Mathf.Max(0, presetChoices.IndexOf(evt.newValue));
                RefreshActiveWorkspace();
            });
            configCard.Add(presetDropdown);

            var strategyField = new EnumField("Extraction Strategy", activePreset.extractionStrategy);
            strategyField.RegisterValueChangedCallback(evt =>
            {
                activePreset.extractionStrategy = (ExtractionStrategy)evt.newValue;
                EditorUtility.SetDirty(settings);
            });
            configCard.Add(strategyField);

            var modeField = new EnumField("Threshold Configuration Mode", activePreset.inputMode);
            modeField.RegisterValueChangedCallback(evt =>
            {
                activePreset.inputMode = (ThresholdInputMode)evt.newValue;
                EditorUtility.SetDirty(settings);
                RefreshActiveWorkspace();
            });
            configCard.Add(modeField);

            var fadeField = new EnumField("LOD Fade Mode", activePreset.fadeMode);
            fadeField.RegisterValueChangedCallback(evt =>
            {
                activePreset.fadeMode = (LODFadeMode)evt.newValue;
                EditorUtility.SetDirty(settings);
            });
            configCard.Add(fadeField);

            var maxTiersSlider = new SliderInt("Max LOD Tiers to Generate", 2, 6) { value = activePreset.maxLodTiers };
            maxTiersSlider.showInputField = true;
            maxTiersSlider.RegisterValueChangedCallback(evt =>
            {
                activePreset.maxLodTiers = evt.newValue;
                EditorUtility.SetDirty(settings);
                RefreshActiveWorkspace();
            });
            configCard.Add(maxTiersSlider);

            BuildPresetThresholdSliders(configCard, activePreset);

            var actionRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 12 } };
            var convertBtn = new Button(() =>
            {
                if (PrefabConversionService.ConvertPrefabAsset(currentReport, activePreset, settings, out string msg))
                {
                    ScanCurrentPrefab();
                    SetStatus(msg);
                    RefreshActiveWorkspace();
                }
                else
                {
                    SetStatus(msg);
                    EditorUtility.DisplayDialog("MeshLOD Studio — Conversion", msg, "OK");
                }
            })
            {
                text = $"Convert Selected Objects in '{currentReport.prefabName}' & Save Prefab"
            };
            convertBtn.AddToClassList("primary-button");
            actionRow.Add(convertBtn);

            if (currentReport.hasBackupRecord)
            {
                var revertBtn = new Button(ExecuteRollbackOnCurrentPrefab) { text = "Revert Prefab to Original Mesh LOD State" };
                revertBtn.AddToClassList("danger-button");
                revertBtn.style.marginLeft = 10;
                actionRow.Add(revertBtn);
            }

            configCard.Add(actionRow);
            workspaceScroll.Add(configCard);
        }

        // ====================================================================
        // 4. LOD GROUP EDITOR WORKSPACE
        // ====================================================================
        private void BuildLODGroupEditorView()
        {
            if (currentReport == null || currentReport.prefabAsset == null)
            {
                var card = CreateCard("LOD Group Editor — No Prefab Selected");
                card.Add(new Label("Select a converted Prefab asset with one or more LODGroup components to fine-tune individual transitions."));
                workspaceScroll.Add(card);
                return;
            }

            LODGroup[] groups = currentReport.prefabAsset.GetComponentsInChildren<LODGroup>(true);
            if (groups.Length == 0)
            {
                var emptyCard = CreateCard($"No LODGroups Found on '{currentReport.prefabName}'");
                emptyCard.Add(new Label("This prefab does not contain any LODGroup components yet. Convert its Mesh LODs in the 'Mesh LOD Converter' tab first."));
                workspaceScroll.Add(emptyCard);
                return;
            }

            foreach (LODGroup group in groups)
            {
                string relPath = AnimationUtility.CalculateTransformPath(group.transform, currentReport.prefabAsset.transform);
                string groupTitle = string.IsNullOrEmpty(relPath) ? $"{group.name} (Root)" : relPath;

                var card = CreateCard($"LODGroup: {groupTitle}");
                LOD[] lods = group.GetLODs();
                float objSize = group.size;

                card.Add(BuildVisualLodTransitionBar(lods));
                card.Add(new Label($"World Space Size: {objSize:0.00}m  |  LOD Count: {lods.Length}  |  Note: World-space distances are computed for a {settings.defaultReferenceCameraFov}° FOV perspective camera (QualitySettings.lodBias = {QualitySettings.lodBias:0.##}).")
                {
                    style = { fontSize = 11, color = new Color(0.62f, 0.66f, 0.72f), marginBottom = 8 }
                });

                var fadeEnum = new EnumField("Fade Mode", group.fadeMode);
                var crossToggle = new Toggle("Animate Cross-Fading") { value = group.animateCrossFading };

                float[] editedHeights = new float[lods.Length];
                float[] editedWidths = new float[lods.Length];

                for (int i = 0; i < lods.Length; i++)
                {
                    int tierIdx = i;
                    editedHeights[tierIdx] = lods[tierIdx].screenRelativeTransitionHeight;
                    editedWidths[tierIdx] = lods[tierIdx].fadeTransitionWidth;

                    float approxDist = DistanceThresholdCalculator.ScreenRelativeHeightToDistance(
                        editedHeights[tierIdx],
                        objSize,
                        settings.defaultReferenceCameraFov,
                        QualitySettings.lodBias);

                    var tierBox = new VisualElement
                    {
                        style =
                        {
                            backgroundColor = new Color(0.09f, 0.10f, 0.12f),
                            paddingLeft = 10,
                            paddingRight = 10,
                            paddingTop = 8,
                            paddingBottom = 8,
                            marginBottom = 6,
                            borderTopLeftRadius = 4,
                            borderTopRightRadius = 4,
                            borderBottomLeftRadius = 4,
                            borderBottomRightRadius = 4
                        }
                    };

                    int triCount = CountTrianglesInRenderers(lods[tierIdx].renderers);
                    var distLabel = new Label($"LOD {tierIdx} ({triCount} tris) — Screen Height: {(editedHeights[tierIdx] * 100f):0.1}%  (~{approxDist:0.1}m)");
                    distLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                    tierBox.Add(distLabel);

                    var heightSlider = new Slider("Transition Screen Height (0..1)", 0.001f, 0.99f)
                    {
                        value = editedHeights[tierIdx],
                        showInputField = true
                    };

                    var distanceField = new FloatField("Approximate Distance (Metres)")
                    {
                        value = Mathf.Round(approxDist * 10f) / 10f
                    };

                    heightSlider.RegisterValueChangedCallback(evt =>
                    {
                        editedHeights[tierIdx] = evt.newValue;
                        float d = DistanceThresholdCalculator.ScreenRelativeHeightToDistance(
                            evt.newValue,
                            objSize,
                            settings.defaultReferenceCameraFov,
                            QualitySettings.lodBias);
                        distanceField.SetValueWithoutNotify(Mathf.Round(d * 10f) / 10f);
                        distLabel.text = $"LOD {tierIdx} ({triCount} tris) — Screen Height: {(evt.newValue * 100f):0.1}%  (~{d:0.1}m)";
                    });

                    distanceField.RegisterValueChangedCallback(evt =>
                    {
                        float h = DistanceThresholdCalculator.DistanceToScreenRelativeHeight(
                            Mathf.Max(0.1f, evt.newValue),
                            objSize,
                            settings.defaultReferenceCameraFov,
                            false,
                            10f,
                            QualitySettings.lodBias);
                        editedHeights[tierIdx] = h;
                        heightSlider.SetValueWithoutNotify(h);
                        distLabel.text = $"LOD {tierIdx} ({triCount} tris) — Screen Height: {(h * 100f):0.1}%  (~{evt.newValue:0.1}m)";
                    });

                    var widthSlider = new Slider("Cross-Fade Transition Width", 0f, 1f)
                    {
                        value = editedWidths[tierIdx],
                        showInputField = true
                    };
                    widthSlider.RegisterValueChangedCallback(evt => editedWidths[tierIdx] = evt.newValue);

                    tierBox.Add(heightSlider);
                    tierBox.Add(distanceField);
                    tierBox.Add(widthSlider);
                    card.Add(tierBox);
                }

                card.Add(fadeEnum);
                card.Add(crossToggle);

                var applyBtn = new Button(() =>
                {
                    ApplyLodGroupEditsToPrefab(
                        currentReport.assetPath,
                        relPath,
                        editedHeights,
                        editedWidths,
                        (LODFadeMode)fadeEnum.value,
                        crossToggle.value);
                })
                {
                    text = $"Save LODGroup Changes to '{groupTitle}'"
                };
                applyBtn.AddToClassList("primary-button");
                applyBtn.style.marginTop = 8;
                card.Add(applyBtn);

                workspaceScroll.Add(card);
            }
        }

        private void ApplyLodGroupEditsToPrefab(
            string prefabAssetPath,
            string relativeTransformPath,
            float[] newHeights,
            float[] newFadeWidths,
            LODFadeMode fadeMode,
            bool animateCrossFade)
        {
            DistanceThresholdCalculator.EnforceStrictlyDescendingThresholds(newHeights);
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabAssetPath);
                Transform target = string.IsNullOrEmpty(relativeTransformPath)
                    ? root.transform
                    : root.transform.Find(relativeTransformPath);

                if (target != null)
                {
                    LODGroup lg = target.GetComponent<LODGroup>();
                    if (lg != null)
                    {
                        LOD[] lods = lg.GetLODs();
                        for (int i = 0; i < lods.Length && i < newHeights.Length; i++)
                        {
                            lods[i].screenRelativeTransitionHeight = newHeights[i];
                            lods[i].fadeTransitionWidth = newFadeWidths[i];
                        }
                        lg.fadeMode = fadeMode;
                        lg.animateCrossFading = animateCrossFade;
                        lg.SetLODs(lods);
                        lg.RecalculateBounds();
                    }
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabAssetPath, out bool saved);
                if (saved)
                {
                    SetStatus($"Updated LODGroup thresholds on '{prefabAssetPath}'.");
                }
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }

            ScanCurrentPrefab();
            RefreshActiveWorkspace();
        }

        // ====================================================================
        // 5. BATCH PREFAB OPTIMIZER WORKSPACE
        // ====================================================================
        private void BuildBatchOptimizerView()
        {
            var topCard = CreateCard("Batch Prefab Optimizer");
            topCard.Add(new Label("Select multiple Prefab assets in the Project window and click 'Scan Selected Project Prefabs' to queue them for deduplicated batch conversion."));

            var btnRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 8 } };
            var scanSelBtn = new Button(ScanProjectSelectionForBatch) { text = "Scan Selected Project Prefabs" };
            scanSelBtn.AddToClassList("secondary-button");
            scanSelBtn.style.marginRight = 8;

            var runBatchBtn = new Button(ExecuteBatchConversion) { text = $"Convert Compatible Prefabs in Batch ({batchReports.Count})" };
            runBatchBtn.AddToClassList("primary-button");

            btnRow.Add(scanSelBtn);
            btnRow.Add(runBatchBtn);
            topCard.Add(btnRow);
            workspaceScroll.Add(topCard);

            var listCard = CreateCard($"Queued Prefab Assets ({batchReports.Count})");
            if (batchReports.Count == 0)
            {
                listCard.Add(new Label("No prefabs queued. Select .prefab assets in the Project window and click 'Scan Selected Project Prefabs'."));
            }
            else
            {
                var presetNames = new List<string>();
                foreach (var p in settings.presets) presetNames.Add(p.presetName);

                foreach (PrefabScanReport rep in batchReports)
                {
                    var row = new VisualElement();
                    row.AddToClassList("table-row");

                    row.Add(new Label(rep.prefabName) { style = { width = 180, unityFontStyleAndWeight = FontStyle.Bold } });
                    row.Add(new Label($"{rep.compatibleNodeCount} convertible / {rep.maxDetectedLodLevels} max LODs") { style = { flexGrow = 1 } });

                    var presetPopup = new PopupField<string>(presetNames, rep.selectedPresetName) { style = { width = 140, marginRight = 8 } };
                    presetPopup.RegisterValueChangedCallback(evt => rep.selectedPresetName = evt.newValue);
                    row.Add(presetPopup);

                    string statusText = rep.compatibleNodeCount > 0 ? "COMPATIBLE" : (rep.existingLODGroupCount > 0 ? "HAS LODGROUP" : "UNSUPPORTED");
                    string statusClass = rep.compatibleNodeCount > 0 ? "badge-ok" : (rep.existingLODGroupCount > 0 ? "badge-warn" : "badge-error");
                    row.Add(CreateBadge(statusText, statusClass));

                    listCard.Add(row);
                }
            }
            workspaceScroll.Add(listCard);
        }

        private void ScanProjectSelectionForBatch()
        {
            batchReports.Clear();
            var seenGuids = new HashSet<string>();

            foreach (UnityEngine.Object obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    continue;

                string guid = AssetDatabase.AssetPathToGUID(path);
                if (seenGuids.Contains(guid)) continue;
                seenGuids.Add(guid);

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null)
                {
                    batchReports.Add(PrefabScannerAndValidator.ScanPrefabAsset(prefab, settings));
                }
            }

            SetStatus($"Scanned {batchReports.Count} unique Prefab asset(s) from Project selection.");
            RefreshActiveWorkspace();
        }

        private void ExecuteBatchConversion()
        {
            if (batchReports.Count == 0) return;

            int successCount = 0;
            int skipCount = 0;
            int failCount = 0;

            try
            {
                for (int i = 0; i < batchReports.Count; i++)
                {
                    PrefabScanReport rep = batchReports[i];
                    if (EditorUtility.DisplayCancelableProgressBar(
                        "MeshLOD Studio — Batch Optimizer",
                        $"Processing {rep.prefabName} ({i + 1}/{batchReports.Count})...",
                        (float)i / batchReports.Count))
                    {
                        break;
                    }

                    if (rep.compatibleNodeCount == 0)
                    {
                        skipCount++;
                        continue;
                    }

                    MeshLODPreset preset = settings.GetPresetByName(rep.selectedPresetName);
                    if (PrefabConversionService.ConvertPrefabAsset(rep, preset, settings, out _))
                        successCount++;
                    else
                        failCount++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            ScanProjectSelectionForBatch();
            SetStatus($"Batch complete: {successCount} converted, {skipCount} skipped, {failCount} failed.");
        }

        // ====================================================================
        // 6. SCENE AUTO SETUP WORKSPACE (EXPLICIT OPT-IN)
        // ====================================================================
        private void BuildSceneAutoSetupView()
        {
            var headerCard = CreateCard("Scene Auto Setup (Source-Prefab Deduplicated Workflow)");
            headerCard.Add(new Label("Scans the active scene to discover unique source Prefab assets (e.g., 300 Oak Tree instances -> 1 Oak_Tree.prefab). Converting the source prefab once updates all normal scene instances automatically without duplicating 300 setups."));

            var scanSceneBtn = new Button(() =>
            {
                currentSceneScan = SceneAutoSetupService.ScanActiveScene(settings);
                SetStatus($"Scanned scene '{currentSceneScan.sceneName}': {currentSceneScan.totalPrefabInstances} prefab instances across {currentSceneScan.uniquePrefabAssetCount} unique Prefab assets.");
                RefreshActiveWorkspace();
            })
            {
                text = "Scan Active Scene"
            };
            scanSceneBtn.AddToClassList("primary-button");
            scanSceneBtn.style.marginTop = 8;
            headerCard.Add(scanSceneBtn);
            workspaceScroll.Add(headerCard);

            if (currentSceneScan == null) return;

            var summaryCard = CreateCard($"Scene Scan Summary: {currentSceneScan.sceneName}");
            var metricRow = new VisualElement();
            metricRow.AddToClassList("metric-row");
            metricRow.Add(CreateMetricBox(currentSceneScan.totalPrefabInstances.ToString(), "Total Prefab Instances"));
            metricRow.Add(CreateMetricBox(currentSceneScan.uniquePrefabAssetCount.ToString(), "Unique Source Prefabs"));
            metricRow.Add(CreateMetricBox(currentSceneScan.convertibleUniquePrefabs.ToString(), "Convertible Unique Prefabs"));
            metricRow.Add(CreateMetricBox(currentSceneScan.standaloneMeshObjectsCount.ToString(), "Standalone Mesh Objects"));
            summaryCard.Add(metricRow);
            workspaceScroll.Add(summaryCard);

            var prefabTableCard = CreateCard("Unique Source Prefab Assets Referenced in Scene");
            var presetNames = new List<string>();
            foreach (var p in settings.presets) presetNames.Add(p.presetName);

            foreach (ScenePrefabGroupEntry group in currentSceneScan.uniquePrefabGroups)
            {
                var row = new VisualElement();
                row.AddToClassList("table-row");

                var toggle = new Toggle { value = group.selectedForConversion };
                toggle.SetEnabled(group.compatibleMeshNodesInPrefab > 0);
                toggle.RegisterValueChangedCallback(evt => group.selectedForConversion = evt.newValue);
                row.Add(toggle);

                row.Add(new Label($"{group.prefabName} ({group.instanceCountInScene} instances, {group.overriddenInstanceCount} with overrides)")
                {
                    style = { width = 280, unityFontStyleAndWeight = FontStyle.Bold }
                });

                row.Add(new Label(group.statusSummary) { style = { flexGrow = 1 } });

                var presetField = new PopupField<string>(presetNames, group.assignedPresetName) { style = { width = 135 } };
                presetField.RegisterValueChangedCallback(evt => group.assignedPresetName = evt.newValue);
                row.Add(presetField);

                prefabTableCard.Add(row);
            }

            var applyScenePrefabsBtn = new Button(ExecuteScenePrefabConversionWithConfirmation)
            {
                text = "Convert Selected Unique Source Prefabs (With Confirmation)"
            };
            applyScenePrefabsBtn.AddToClassList("primary-button");
            applyScenePrefabsBtn.style.marginTop = 10;
            prefabTableCard.Add(applyScenePrefabsBtn);
            workspaceScroll.Add(prefabTableCard);

            if (currentSceneScan.standaloneObjects.Count > 0)
            {
                var standaloneCard = CreateCard($"Standalone Non-Prefab Scene Objects ({currentSceneScan.standaloneObjects.Count}) — Unchecked by Default");
                foreach (StandaloneSceneObjectEntry st in currentSceneScan.standaloneObjects)
                {
                    var row = new VisualElement();
                    row.AddToClassList("table-row");
                    var toggle = new Toggle { value = st.selectedForConversion };
                    toggle.RegisterValueChangedCallback(evt => st.selectedForConversion = evt.newValue);
                    row.Add(toggle);
                    row.Add(new Label($"{st.hierarchyPath} ({st.detectedMeshLodCount} Mesh LODs)") { style = { flexGrow = 1 } });
                    standaloneCard.Add(row);
                }

                var convertStandaloneBtn = new Button(() =>
                {
                    int count = 0;
                    MeshLODPreset preset = GetSelectedPreset();
                    foreach (var st in currentSceneScan.standaloneObjects)
                    {
                        if (st.selectedForConversion && SceneAutoSetupService.ConvertStandaloneSceneObject(st, preset, settings, out _))
                            count++;
                    }
                    currentSceneScan = SceneAutoSetupService.ScanActiveScene(settings);
                    SetStatus($"Converted {count} standalone scene object(s).");
                    RefreshActiveWorkspace();
                })
                {
                    text = "Convert Explicitly Selected Standalone Objects"
                };
                convertStandaloneBtn.AddToClassList("secondary-button");
                convertStandaloneBtn.style.marginTop = 8;
                standaloneCard.Add(convertStandaloneBtn);
                workspaceScroll.Add(standaloneCard);
            }
        }

        private void ExecuteScenePrefabConversionWithConfirmation()
        {
            if (currentSceneScan == null) return;

            var selectedGroups = new List<ScenePrefabGroupEntry>();
            int totalInstancesAffected = 0;
            foreach (var g in currentSceneScan.uniquePrefabGroups)
            {
                if (g.selectedForConversion && g.compatibleMeshNodesInPrefab > 0)
                {
                    selectedGroups.Add(g);
                    totalInstancesAffected += g.instanceCountInScene;
                }
            }

            if (selectedGroups.Count == 0)
            {
                EditorUtility.DisplayDialog("MeshLOD Studio", "No compatible source prefabs are selected.", "OK");
                return;
            }

            bool confirm = EditorUtility.DisplayDialog(
                "Confirm Scene Source-Prefab Conversion",
                $"You are about to convert {selectedGroups.Count} unique source Prefab asset(s).\n\n" +
                $"This will automatically update {totalInstancesAffected} scene instance(s) via Unity's prefab inheritance system while preserving instance overrides.\n\n" +
                "Proceed?",
                "Convert Selected Prefabs",
                "Cancel");

            if (!confirm) return;

            int converted = 0;
            for (int i = 0; i < selectedGroups.Count; i++)
            {
                var g = selectedGroups[i];
                PrefabScanReport rep = PrefabScannerAndValidator.ScanPrefabAsset(g.sourcePrefabAsset, settings);
                MeshLODPreset preset = settings.GetPresetByName(g.assignedPresetName);
                if (PrefabConversionService.ConvertPrefabAsset(rep, preset, settings, out _))
                    converted++;
            }

            currentSceneScan = SceneAutoSetupService.ScanActiveScene(settings);
            SetStatus($"Converted {converted} unique source Prefab(s), updating {totalInstancesAffected} scene instances.");
            RefreshActiveWorkspace();
        }

        // ====================================================================
        // 7. PRESET MANAGER WORKSPACE
        // ====================================================================
        private void BuildPresetManagerView()
        {
            var card = CreateCard("Preset Manager (Cross-Project Reusable Presets)");
            MeshLODPreset activePreset = GetSelectedPreset();

            var topBar = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 10 } };
            var presetNames = new List<string>();
            foreach (var p in settings.presets) presetNames.Add(p.presetName);

            var popup = new PopupField<string>(presetNames, Mathf.Clamp(selectedPresetIndex, 0, presetNames.Count - 1))
            {
                style = { width = 220, marginRight = 8 }
            };
            popup.RegisterValueChangedCallback(evt =>
            {
                selectedPresetIndex = Mathf.Max(0, presetNames.IndexOf(evt.newValue));
                RefreshActiveWorkspace();
            });
            topBar.Add(popup);

            var dupBtn = new Button(() =>
            {
                MeshLODPreset clone = activePreset.Clone(activePreset.presetName + " Copy");
                settings.presets.Add(clone);
                selectedPresetIndex = settings.presets.Count - 1;
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                RefreshActiveWorkspace();
            }) { text = "Duplicate" };
            dupBtn.AddToClassList("secondary-button");
            dupBtn.style.marginRight = 6;
            topBar.Add(dupBtn);

            var delBtn = new Button(() =>
            {
                if (!activePreset.isBuiltIn && settings.presets.Count > 1)
                {
                    settings.presets.RemoveAt(selectedPresetIndex);
                    selectedPresetIndex = 0;
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssets();
                    RefreshActiveWorkspace();
                }
            }) { text = "Delete" };
            delBtn.SetEnabled(!activePreset.isBuiltIn);
            delBtn.AddToClassList("danger-button");
            delBtn.style.marginRight = 6;
            topBar.Add(delBtn);

            var exportBtn = new Button(() => ExportPresetToJson(activePreset)) { text = "Export JSON" };
            exportBtn.AddToClassList("secondary-button");
            exportBtn.style.marginRight = 6;
            topBar.Add(exportBtn);

            var importBtn = new Button(ImportPresetFromJson) { text = "Import JSON" };
            importBtn.AddToClassList("secondary-button");
            topBar.Add(importBtn);

            card.Add(topBar);

            var nameField = new TextField("Preset Name") { value = activePreset.presetName };
            nameField.SetEnabled(!activePreset.isBuiltIn);
            nameField.RegisterValueChangedCallback(evt =>
            {
                activePreset.presetName = evt.newValue;
                EditorUtility.SetDirty(settings);
            });
            card.Add(nameField);

            var catField = new EnumField("Category", activePreset.category);
            catField.RegisterValueChangedCallback(evt =>
            {
                activePreset.category = (PresetCategory)evt.newValue;
                EditorUtility.SetDirty(settings);
            });
            card.Add(catField);

            var descField = new TextField("Description") { value = activePreset.description };
            descField.RegisterValueChangedCallback(evt =>
            {
                activePreset.description = evt.newValue;
                EditorUtility.SetDirty(settings);
            });
            card.Add(descField);

            BuildPresetThresholdSliders(card, activePreset);
            workspaceScroll.Add(card);
        }

        private void ExportPresetToJson(MeshLODPreset preset)
        {
            string path = EditorUtility.SaveFilePanel("Export MeshLOD Preset", Application.dataPath, $"{preset.presetName}.meshlodpreset.json", "json");
            if (string.IsNullOrEmpty(path)) return;
            File.WriteAllText(path, JsonUtility.ToJson(preset, true));
            SetStatus($"Exported preset '{preset.presetName}' to '{path}'.");
        }

        private void ImportPresetFromJson()
        {
            string path = EditorUtility.OpenFilePanel("Import MeshLOD Preset", Application.dataPath, "json");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            try
            {
                MeshLODPreset imported = JsonUtility.FromJson<MeshLODPreset>(File.ReadAllText(path));
                if (imported != null && !string.IsNullOrEmpty(imported.presetName))
                {
                    imported.isBuiltIn = false;
                    imported.presetId = Guid.NewGuid().ToString("N");
                    settings.presets.Add(imported);
                    selectedPresetIndex = settings.presets.Count - 1;
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssets();
                    SetStatus($"Imported preset '{imported.presetName}'.");
                    RefreshActiveWorkspace();
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Import Error", $"Failed to parse preset JSON: {ex.Message}", "OK");
            }
        }

        // ====================================================================
        // 8. SETTINGS & VALIDATION WORKSPACE
        // ====================================================================
        private void BuildSettingsView()
        {
            var card = CreateCard("Global MeshLOD Studio Settings");

            var folderField = new TextField("Generated Mesh Assets Folder") { value = settings.generatedAssetsRootFolder };
            folderField.RegisterValueChangedCallback(evt =>
            {
                settings.generatedAssetsRootFolder = evt.newValue;
                EditorUtility.SetDirty(settings);
            });
            card.Add(folderField);

            var fovField = new FloatField("Default Reference Camera FOV (°)") { value = settings.defaultReferenceCameraFov };
            fovField.RegisterValueChangedCallback(evt =>
            {
                settings.defaultReferenceCameraFov = Mathf.Clamp(evt.newValue, 10f, 160f);
                EditorUtility.SetDirty(settings);
            });
            card.Add(fovField);

            var backupToggle = new Toggle("Create Rollback Backup Record Before Conversion") { value = settings.createBackupBeforeConversion };
            backupToggle.RegisterValueChangedCallback(evt =>
            {
                settings.createBackupBeforeConversion = evt.newValue;
                EditorUtility.SetDirty(settings);
            });
            card.Add(backupToggle);

            var skinnedToggle = new Toggle("Allow SkinnedMeshRenderer Conversion (Experimental)") { value = settings.allowSkinnedMeshRendererConversion };
            skinnedToggle.RegisterValueChangedCallback(evt =>
            {
                settings.allowSkinnedMeshRendererConversion = evt.newValue;
                EditorUtility.SetDirty(settings);
            });
            card.Add(skinnedToggle);

            workspaceScroll.Add(card);

            var testCard = CreateCard("Automated 15-Case Verification & Test Suite");
            testCard.Add(new Label("Runs automated verification against temporary synthetic prefabs and meshes (covering single-mesh, multi-child, nested prefabs, variants, overrides, rollback, and batching) and cleans up all temporary assets afterward."));

            var runTestsBtn = new Button(() =>
            {
                string summary = MeshLODStudioTests.RunAllValidationTests();
                SetStatus("Completed 15-case automated verification suite. Check Console for detailed report.");
                EditorUtility.DisplayDialog("MeshLOD Studio — Validation Suite Results", summary, "OK");
            })
            {
                text = "Run All 15 Automated Validation Tests"
            };
            runTestsBtn.AddToClassList("primary-button");
            runTestsBtn.style.marginTop = 8;
            testCard.Add(runTestsBtn);
            workspaceScroll.Add(testCard);
        }

        // ====================================================================
        // SHARED UI HELPERS
        // ====================================================================
        private void BuildEmbeddedPreviewElement(VisualElement parent, Mesh mesh, Material[] materials)
        {
            previewController.SetTarget(mesh, materials);
            MeshLODPreset activePreset = GetSelectedPreset();

            var previewHeader = new Label("Interactive 3D LOD & Distance Preview (Non-Destructive)")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 10, marginBottom = 4 }
            };
            parent.Add(previewHeader);

            var lodBtnBar = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 6 } };
            var autoBtn = new Button(() => previewController.forcedPreviewLod = -1) { text = "Auto (By Distance)" };
            autoBtn.AddToClassList("secondary-button");
            lodBtnBar.Add(autoBtn);

            for (int l = 0; l < mesh.lodCount; l++)
            {
                int lodIdx = l;
                var btn = new Button(() => previewController.forcedPreviewLod = lodIdx) { text = $"LOD{lodIdx}" };
                btn.AddToClassList("secondary-button");
                btn.style.marginLeft = 4;
                lodBtnBar.Add(btn);
            }

            var wireToggle = new Toggle("Wireframe") { value = previewController.showWireframe, style = { marginLeft = 8 } };
            wireToggle.RegisterValueChangedCallback(evt => previewController.showWireframe = evt.newValue);
            lodBtnBar.Add(wireToggle);
            parent.Add(lodBtnBar);

            var distSlider = new Slider("Simulated Camera Distance (m)", 0.5f, 200f)
            {
                value = previewController.simulatedDistanceMeters,
                showInputField = true
            };
            distSlider.RegisterValueChangedCallback(evt => previewController.simulatedDistanceMeters = evt.newValue);
            parent.Add(distSlider);

            var imguiPreview = new IMGUIContainer(() =>
            {
                Rect r = GUILayoutUtility.GetRect(390f, 220f);
                Event e = Event.current;
                if (e.type == EventType.MouseDrag && r.Contains(e.mousePosition))
                {
                    previewController.orbitAngles.y += e.delta.x * 0.5f;
                    previewController.orbitAngles.x = Mathf.Clamp(previewController.orbitAngles.x + e.delta.y * 0.5f, -85f, 85f);
                    e.Use();
                    Repaint();
                }

                Texture tex = previewController.RenderPreview(r, activePreset);
                if (tex != null)
                {
                    GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, false);
                }

                string modeStr = previewController.forcedPreviewLod < 0 ? "Auto" : "Locked";
                string overlayText = previewController.activeRenderedLodIndex >= mesh.lodCount
                    ? $"[CULLED]  Distance: {previewController.simulatedDistanceMeters:0.1}m  |  Screen Height: {(previewController.activeScreenRelativeHeight * 100f):0.1}%"
                    : $"[{modeStr}: LOD{previewController.activeRenderedLodIndex}]  {previewController.activeRenderedTriangles} tris  |  Dist: {previewController.simulatedDistanceMeters:0.1}m  |  Screen Height: {(previewController.activeScreenRelativeHeight * 100f):0.1}%";

                EditorGUI.DrawRect(new Rect(r.x, r.yMax - 22f, r.width, 22f), new Color(0f, 0f, 0f, 0.7f));
                GUI.Label(new Rect(r.x + 8f, r.yMax - 20f, r.width - 16f, 20f), overlayText, EditorStyles.whiteMiniLabel);
            });
            parent.Add(imguiPreview);
        }

        private void BuildPresetThresholdSliders(VisualElement container, MeshLODPreset preset)
        {
            while (preset.tierScreenHeights.Count < 5) preset.tierScreenHeights.Add(0.02f);
            while (preset.tierDistancesMeters.Count < 5) preset.tierDistancesMeters.Add(100f);

            int count = Mathf.Clamp(preset.maxLodTiers - 1, 1, 5);
            for (int i = 0; i < count; i++)
            {
                int idx = i;
                if (preset.inputMode == ThresholdInputMode.ScreenRelativePercentage)
                {
                    var s = new Slider($"LOD{idx} → LOD{idx + 1} Screen Height", 0.01f, 0.95f)
                    {
                        value = preset.tierScreenHeights[idx],
                        showInputField = true
                    };
                    s.RegisterValueChangedCallback(evt =>
                    {
                        preset.tierScreenHeights[idx] = evt.newValue;
                        EditorUtility.SetDirty(settings);
                    });
                    container.Add(s);
                }
                else
                {
                    var d = new FloatField($"LOD{idx} → LOD{idx + 1} Distance (m)")
                    {
                        value = preset.tierDistancesMeters[idx]
                    };
                    d.RegisterValueChangedCallback(evt =>
                    {
                        preset.tierDistancesMeters[idx] = Mathf.Max(1f, evt.newValue);
                        EditorUtility.SetDirty(settings);
                    });
                    container.Add(d);
                }
            }

            if (preset.inputMode == ThresholdInputMode.ScreenRelativePercentage)
            {
                var cullSlider = new Slider("Final Culling Screen Height", 0.001f, 0.25f)
                {
                    value = preset.cullScreenRelativeHeight,
                    showInputField = true
                };
                cullSlider.RegisterValueChangedCallback(evt =>
                {
                    preset.cullScreenRelativeHeight = evt.newValue;
                    EditorUtility.SetDirty(settings);
                });
                container.Add(cullSlider);
            }
            else
            {
                var cullDist = new FloatField("Final Culling Distance (m)")
                {
                    value = preset.cullDistanceMeters
                };
                cullDist.RegisterValueChangedCallback(evt =>
                {
                    preset.cullDistanceMeters = Mathf.Max(5f, evt.newValue);
                    EditorUtility.SetDirty(settings);
                });
                container.Add(cullDist);
            }
        }

        private VisualElement BuildVisualLodTransitionBar(LOD[] lods)
        {
            var bar = new VisualElement
            {
                style =
                {
                    height = 26,
                    flexDirection = FlexDirection.Row,
                    borderTopLeftRadius = 4,
                    borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4,
                    borderBottomRightRadius = 4,
                    overflow = Overflow.Hidden,
                    marginBottom = 8
                }
            };

            float prevHeight = 1.0f;
            for (int i = 0; i < lods.Length; i++)
            {
                float h = lods[i].screenRelativeTransitionHeight;
                float span = Mathf.Max(0.02f, prevHeight - h);
                prevHeight = h;

                var seg = new VisualElement
                {
                    style =
                    {
                        flexGrow = span,
                        backgroundColor = LodBarColors[i % LodBarColors.Length],
                        justifyContent = Justify.Center,
                        alignItems = Align.Center
                    }
                };
                seg.Add(new Label($"LOD{i} ({(h * 100f):0}%)") { style = { fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold, color = Color.white } });
                bar.Add(seg);
            }

            float cullSpan = Mathf.Max(0.03f, prevHeight);
            var cullSeg = new VisualElement
            {
                style =
                {
                    flexGrow = cullSpan,
                    backgroundColor = new Color(0.45f, 0.14f, 0.14f),
                    justifyContent = Justify.Center,
                    alignItems = Align.Center
                }
            };
            cullSeg.Add(new Label("Culled") { style = { fontSize = 10, color = new Color(0.95f, 0.7f, 0.7f) } });
            bar.Add(cullSeg);

            return bar;
        }

        private void ExecuteRollbackOnCurrentPrefab()
        {
            if (currentReport == null || currentReport.prefabAsset == null) return;
            if (BackupAndRollbackManager.RollbackPrefab(currentReport.prefabAsset, out string msg, settings))
            {
                ScanCurrentPrefab();
                SetStatus(msg);
                RefreshActiveWorkspace();
            }
            else
            {
                SetStatus(msg);
                EditorUtility.DisplayDialog("Rollback", msg, "OK");
            }
        }

        private MeshLODPreset GetSelectedPreset()
        {
            if (settings.presets == null || settings.presets.Count == 0)
                settings.InitializeDefaultPresets();
            selectedPresetIndex = Mathf.Clamp(selectedPresetIndex, 0, settings.presets.Count - 1);
            return settings.presets[selectedPresetIndex];
        }

        private static VisualElement CreateCard(string title)
        {
            var card = new VisualElement();
            card.AddToClassList("card");
            var label = new Label(title);
            label.AddToClassList("card-title");
            card.Add(label);
            return card;
        }

        private static VisualElement CreateMetricBox(string value, string label)
        {
            var box = new VisualElement();
            box.AddToClassList("metric-box");
            var valLbl = new Label(value);
            valLbl.AddToClassList("metric-value");
            var nameLbl = new Label(label);
            nameLbl.AddToClassList("metric-label");
            box.Add(valLbl);
            box.Add(nameLbl);
            return box;
        }

        private static Label CreateBadge(string text, string className)
        {
            var lbl = new Label(text);
            lbl.AddToClassList(className);
            return lbl;
        }

        private static string FormatMaterialNames(Material[] mats)
        {
            if (mats == null || mats.Length == 0) return "None";
            var names = new List<string>();
            foreach (var m in mats) names.Add(m != null ? m.name : "Missing");
            return string.Join(", ", names);
        }

        private static int CountTrianglesInRenderers(Renderer[] renderers)
        {
            if (renderers == null) return 0;
            int total = 0;
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    Mesh m = mf.sharedMesh;
                    int forced = r.forceMeshLod;
                    if (m.lodCount > 1 && forced >= 0 && forced < m.lodCount)
                    {
                        for (int s = 0; s < m.subMeshCount; s++)
                            total += (int)m.GetLod(s, forced).indexCount / 3;
                    }
                    else
                    {
                        total += MeshLODDetectorAndExtractor.GetBaseTriangleCount(m);
                    }
                }
            }
            return total;
        }
    }
}
#endif