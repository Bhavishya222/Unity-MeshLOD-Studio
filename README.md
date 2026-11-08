# Unity MeshLOD Studio

A professional, Prefab-first LOD management and conversion tool for Unity 6.3+.


Unity's built-in `Generate Mesh LODs` feature is highly performant but lacks support for Static Batching, GPU Instancing, per-LOD material overrides, and custom transition thresholds. MeshLOD Studio solves this by losslessly extracting generated Mesh LOD index buffers into compact, standalone meshes and automatically constructing fully configurable `LODGroup` setups directly on your Prefab assets.

## Core Features
* **Prefab-First Workflow:** Safely modifies the source `.prefab` asset. All scene instances automatically inherit the changes without breaking instance-specific overrides.
* **Lossless LOD Extraction:** Uses Unity's `Mesh.AcquireReadOnlyMeshData` to extract and vertex-compact individual LOD levels without modifying your original FBX models.
* **Zero-Conflict LODGroups:** Preserves your original `MeshFilter`/`MeshRenderer` for LOD0 to maintain FileIDs, scripts, and scene material overrides, building child nodes for LOD1+.
* **Batch Optimizer & Scene Auto Setup:** Select hundreds of prefabs to batch process, or scan an entire scene to automatically group and update unique source prefabs.
* **Preset Manager:** Save, reuse, export, and import JSON presets (e.g., High Quality, Balanced, Performance) mapping screen-relative heights or real-world distances.

## Installation
1. Download or clone this repository.
2. Place the `Assets/Editor/MeshLODStudio` folder into your Unity project.
3. Allow Unity to compile the included `.asmdef` file.
4. Open via **Tools > MeshLOD Studio > Open Studio**.

## Usage
1. Ensure your 3D models have **Generate mesh LODs** enabled in their Model Importer (or use the 1-click enable button inside the tool).
2. Open **Tools > MeshLOD Studio > Open Studio**.
3. Select a `.prefab` asset in your Project window.
4. Review the structural analysis in the **Prefab Studio** tab.
5. Navigate to the **Mesh LOD Converter** tab, select an optimization Preset, and click **Convert Selected Objects & Save Prefab**.

---
Built by [Pinwheels Studios](https://github.com/Bhavishya222). Released under the MIT License.
