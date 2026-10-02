// @ts-check
// Three.js viewport. Owns rendering, camera, hover, ghost preview and raycasting.
// C# owns the build model and its rules: this module asks it "can I place here?" / "place here",
// and draws whatever parts C# tells it were added or removed.
import * as THREE from '../lib/three/three.module.js';
import { OrbitControls } from '../lib/three/addons/controls/OrbitControls.js';
import { mergeGeometries } from '../lib/three/addons/utils/BufferGeometryUtils.js';

/**
 * @typedef {{ id: string, width: number, depth: number, heightPlates: number, hasStuds: boolean }} PartDef
 * @typedef {{ id: number, hex: string, isTransparent: boolean }} ColorDef
 * @typedef {{ id: number, partId: string, x: number, y: number, z: number, rotation: number, colorId: number }} PlacedDto
 * @typedef {object} ViewportOptions
 * @property {number} baseplateWidth  studs along x
 * @property {number} baseplateDepth  studs along z
 * @property {number} plateHeight     world units per plate
 * @property {number} studDiameter
 * @property {number} studHeight
 * @property {number} partInset       gap trimmed from each side of a part
 * @property {PartDef[]} parts
 * @property {ColorDef[]} colors
 * @typedef {{ invokeMethodAsync(name: string, ...args: any[]): Promise<any>, invokeMethod(name: string, ...args: any[]): any }} DotNetRef
 * @typedef {{ x: number, y: number, z: number }} Cell
 */

const CLICK_TOLERANCE_PX = 5;
const VALID_COLOR = 0x4ade80;
const INVALID_COLOR = 0xef4444;
/** Alignment guides sit this far inside the ghost's footprint, so they run through parts below, not the gaps between them. */
const GUIDE_INSET = 0.025;
/** Lifts the footprint outline off the baseplate surface so it doesn't flicker against it. */
const GUIDE_LIFT = 0.01;

/**
 * @param {HTMLElement} host
 * @param {ViewportOptions} o
 * @param {DotNetRef} dotnet
 */
export function createViewport(host, o, dotnet) {
    const partDefs = new Map(o.parts.map(p => [p.id, p]));
    /** Calls into C#, logging failures rather than leaving an unhandled rejection. @param {string} method @param {...any} args */
    const call = (method, ...args) => dotnet.invokeMethodAsync(method, ...args).catch(err => {
        console.error(`[BrickForge] ${method} failed`, err);
        return undefined;
    });
    // Baseplate size can change (resize, new build, import); see setBaseplate().
    let W = o.baseplateWidth, D = o.baseplateDepth;
    const PH = o.plateHeight;

    const renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(window.devicePixelRatio);
    renderer.shadowMap.enabled = true;
    renderer.toneMapping = THREE.NeutralToneMapping; // keeps brick colours saturated, unlike ACES
    host.appendChild(renderer.domElement);
    const canvas = renderer.domElement;

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x1d2330);

    const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 1);
    const homePosition = new THREE.Vector3();

    const controls = new OrbitControls(camera, canvas);
    controls.enableDamping = true;
    controls.minDistance = 2;
    controls.maxPolarAngle = Math.PI * 0.49; // don't go under the baseplate
    // Left mouse is reserved for placing bricks; orbit on right, pan on middle.
    controls.mouseButtons = { LEFT: null, MIDDLE: THREE.MOUSE.PAN, RIGHT: THREE.MOUSE.ROTATE };

    scene.add(new THREE.HemisphereLight(0xffffff, 0x445066, 1.2));
    const sun = new THREE.DirectionalLight(0xffffff, 2.2);
    sun.castShadow = true;
    sun.shadow.mapSize.set(2048, 2048);
    sun.shadow.bias = -0.0005;
    scene.add(sun);

    /** Camera limits, home view and shadow coverage all scale with the baseplate. */
    function fitToBaseplate() {
        const span = Math.max(W, D);
        camera.far = span * 20;
        camera.updateProjectionMatrix();
        homePosition.set(span * 0.7, span * 0.75, span * 1.0);
        controls.maxDistance = span * 4;
        sun.position.set(span * 0.4, span, span * 0.25);
        const half = span * 0.6;
        Object.assign(sun.shadow.camera, { left: -half, right: half, top: half, bottom: -half, near: 1, far: span * 3 });
        sun.shadow.camera.updateProjectionMatrix();
    }
    fitToBaseplate();
    camera.position.copy(homePosition);
    controls.update();

    let baseplate = createBaseplate(o);
    scene.add(baseplate.slab);

    const geometries = new PartGeometries(o, partDefs);
    const materials = new ColorMaterials(o.colors);
    let parts = new PartRenderer(scene, geometries, materials, o, baseplate.material);

    /**
     * Rebuilds everything that depends on the baseplate size. All parts are dropped;
     * the change that resized the baseplate re-adds them at their new positions.
     * @param {number} width @param {number} depth
     */
    function setBaseplate(width, depth) {
        o.baseplateWidth = W = width;
        o.baseplateDepth = D = depth;
        parts.dispose();
        scene.remove(baseplate.slab);
        baseplate.slab.geometry.dispose();
        baseplate.material.dispose();
        baseplate = createBaseplate(o);
        scene.add(baseplate.slab);
        parts = new PartRenderer(scene, geometries, materials, o, baseplate.material);
        fitToBaseplate();
        resetView();
    }

    // ---- tool, ghost & highlight -----------------------------------------------------------
    /**
     * The current tool, from C#. `ghost` is what a click in build mode would place — a group of
     * one or more parts — flat as [catalogIndex, dx, dy, dz, rotation, colorId] per part, with
     * offsets from the group's minimum corner; `ghostSize` is its [x, y (plates), z] extent.
     * @typedef {{ mode: 'build' | 'paint' | 'select', ghost: number[], ghostSize: [number, number, number], selection: number[] }} Tool
     */
    /** @type {Tool | null} */
    let tool = null;
    const ghostLine = new THREE.LineBasicMaterial({ color: VALID_COLOR });
    const ghostInvalid = new THREE.MeshStandardMaterial({ color: INVALID_COLOR, transparent: true, opacity: 0.35, depthWrite: false });
    /** See-through version of each colour, for the ghost. @type {Map<number, THREE.MeshStandardMaterial>} */
    const ghostMaterials = new Map();
    const ghost = new THREE.Group();
    ghost.visible = false;
    scene.add(ghost);
    let ghostValid = true;

    /** @param {number} colorId */
    function ghostMaterial(colorId) {
        let m = ghostMaterials.get(colorId);
        if (!m) {
            m = new THREE.MeshStandardMaterial({ transparent: true, opacity: 0.6, depthWrite: false });
            m.color.copy(/** @type {THREE.MeshStandardMaterial} */ (materials.get(colorId)).color);
            ghostMaterials.set(colorId, m);
        }
        return m;
    }

    function rebuildGhost() {
        ghost.children.forEach(c => { if (c instanceof THREE.LineSegments) c.geometry.dispose(); });
        ghost.clear();
        if (!tool) return;
        const g = tool.ghost;
        for (let i = 0; i < g.length; i += 6) {
            const def = o.parts[g[i]];
            const mesh = new THREE.Mesh(geometries.model(def.id), ghostMaterial(g[i + 5]));
            mesh.userData.colorId = g[i + 5];
            const [sx, sz] = footprint(def, g[i + 4]);
            mesh.position.set(g[i + 1] + sx / 2, g[i + 2] * PH, g[i + 3] + sz / 2);
            mesh.rotation.y = -g[i + 4] * Math.PI / 2;
            ghost.add(mesh);
        }
        const [sx, sy, sz] = tool.ghostSize;
        const box = new THREE.BoxGeometry(sx, sy * PH, sz);
        box.translate(sx / 2, sy * PH / 2, sz / 2);
        ghost.add(new THREE.LineSegments(new THREE.EdgesGeometry(box), ghostLine));
        box.dispose();
        paintGhost(ghostValid);
    }

    /** @param {boolean} valid */
    function paintGhost(valid) {
        ghostValid = valid;
        for (const child of ghost.children) {
            if (child instanceof THREE.Mesh) child.material = valid ? ghostMaterial(child.userData.colorId) : ghostInvalid;
        }
        ghostLine.color.setHex(valid ? VALID_COLOR : INVALID_COLOR);
        guideMaterial.color.setHex(valid ? VALID_COLOR : INVALID_COLOR);
    }

    // Alignment guides: while the ghost is off the ground, lines drop from its bottom corners to the
    // baseplate and its footprint is outlined there. Depth-tested, so parts in the way hide them.
    const guideMaterial = new THREE.LineBasicMaterial({ color: VALID_COLOR, transparent: true, opacity: 0.55 });
    const guidePositions = new THREE.Float32BufferAttribute(new Float32Array(16 * 3), 3); // 4 drops + 4 edges
    const guides = new THREE.LineSegments(new THREE.BufferGeometry().setAttribute('position', guidePositions), guideMaterial);
    guides.visible = false;
    scene.add(guides);

    function updateGuides() {
        guides.visible = ghost.visible && !!tool && !!candidate && candidate.y > 0;
        if (!guides.visible || !tool || !candidate) return;
        const [sx, , sz] = tool.ghostSize;
        const x0 = candidate.x - W / 2 + GUIDE_INSET, x1 = candidate.x + sx - W / 2 - GUIDE_INSET;
        const z0 = candidate.z - D / 2 + GUIDE_INSET, z1 = candidate.z + sz - D / 2 - GUIDE_INSET;
        const bottom = candidate.y * PH;
        const corners = [[x0, z0], [x1, z0], [x1, z1], [x0, z1]];
        corners.forEach(([x, z], i) => {
            const [nx, nz] = corners[(i + 1) % 4];
            guidePositions.setXYZ(i * 4, x, bottom, z);          // drop line
            guidePositions.setXYZ(i * 4 + 1, x, GUIDE_LIFT, z);
            guidePositions.setXYZ(i * 4 + 2, x, GUIDE_LIFT, z);  // footprint edge
            guidePositions.setXYZ(i * 4 + 3, nx, GUIDE_LIFT, nz);
        });
        guidePositions.needsUpdate = true;
        guides.geometry.computeBoundingSphere();
    }

    // Selected parts get a translucent blue shell.
    const selectionMaterial = new THREE.MeshBasicMaterial({ color: 0x4da3ff, transparent: true, opacity: 0.28, depthWrite: false });
    const selectionShell = new THREE.BoxGeometry(1, 1, 1);
    selectionShell.translate(0, 0.5, 0);
    /** @type {InstanceBatch<number> | null} */
    let selectionBatch = null;

    function updateSelection() {
        selectionBatch?.dispose();
        selectionBatch = new InstanceBatch(scene, selectionShell, selectionMaterial, false);
        const pad = 0.06;
        for (const id of tool?.selection ?? []) {
            const rec = parts.record(id);
            if (!rec) continue; // e.g. hidden while being moved
            scratchObject.position.set(rec.x + rec.sx / 2 - W / 2, rec.y * PH - pad / 2, rec.z + rec.sz / 2 - D / 2);
            scratchObject.rotation.set(0, 0, 0);
            scratchObject.scale.set(rec.sx + pad, rec.h * PH + o.studHeight + pad, rec.sz + pad);
            scratchObject.updateMatrix();
            selectionBatch.add(id, scratchObject.matrix);
        }
        scratchObject.scale.set(1, 1, 1);
        selectionBatch.mesh.renderOrder = 1;
    }
    // Outline around the part a click would act on (paint, eyedropper, pick up).
    const unitBox = new THREE.BoxGeometry(1, 1, 1);
    unitBox.translate(0, 0.5, 0);
    const highlightMaterial = new THREE.LineBasicMaterial({ color: 0xffffff });
    const highlight = new THREE.LineSegments(new THREE.EdgesGeometry(unitBox), highlightMaterial);
    unitBox.dispose();
    highlight.visible = false;
    scene.add(highlight);

    const modifiers = { alt: false, shift: false };

    /** Whether a click would act on the hovered part rather than place the ghost. */
    const targetingPart = () => tool?.mode !== 'build' || modifiers.alt || modifiers.shift;

    function updateHighlight() {
        const rec = hoveredPartId !== null && targetingPart() ? parts.record(hoveredPartId) : undefined;
        highlight.visible = !!rec;
        canvas.style.cursor = rec ? 'pointer' : '';
        if (!rec) return;
        const pad = 0.04;
        highlight.scale.set(rec.sx + pad, rec.h * PH + o.studHeight + pad, rec.sz + pad);
        highlight.position.set(rec.x + rec.sx / 2 - W / 2, rec.y * PH - pad / 2, rec.z + rec.sz / 2 - D / 2);
    }

    // ---- hover & candidate ---------------------------------------------------------------
    const raycaster = new THREE.Raycaster();
    const pointer = new THREE.Vector2();
    let pointerInside = false;
    /** @type {Cell | null} */
    let candidate = null;
    /** @type {number | null} part under the cursor */
    let hoveredPartId = null;
    let hoverSeq = 0;

    function updateHover() {
        const hit = pointerInside ? raycast() : null;
        hoveredPartId = hit?.partId ?? null;
        updateHighlight();

        const aimingAtPart = hoveredPartId !== null && (modifiers.alt || modifiers.shift);
        const next = hit && tool?.mode === 'build' && !aimingAtPart && !boxSelect ? aim(hit) : null;
        if (!next) {
            // Checked before the same-cell test: refreshHover() resets candidate to null, so a
            // mode switch would otherwise look like "no change" and leave the ghost showing.
            candidate = null;
            ghost.visible = false;
            guides.visible = false;
            return;
        }
        if (sameCell(next, candidate)) return;

        candidate = next;
        ghost.position.set(candidate.x - W / 2, candidate.y * PH, candidate.z - D / 2);
        ghost.visible = true;
        updateGuides();

        const seq = ++hoverSeq;
        const cell = candidate;
        // Keep the previous colouring until C# answers; the round trip is a few ms.
        call('CanPlace', cell.x, cell.y, cell.z).then(valid => {
            if (seq === hoverSeq) paintGhost(valid);
        });
    }

    /**
     * @typedef {{ point: THREE.Vector3, partId: number | null }} Hit
     * @returns {Hit | null}
     */
    function raycast() {
        raycaster.setFromCamera(pointer, camera);
        const hits = raycaster.intersectObjects([...parts.meshes(), baseplate.slab], false);
        const first = hits[0];
        if (!first) return null;
        const partId = first.object instanceof THREE.InstancedMesh && first.instanceId !== undefined
            ? parts.partIdAt(first.object, first.instanceId)
            : null;
        return { point: first.point, partId };
    }

    /**
     * Where the ghost would go, as its minimum corner, for what the cursor is over. C# decides (see
     * Aiming.cs); the call is synchronous, which Blazor WebAssembly allows, so the ghost never lags.
     * @param {Hit} hit @returns {Cell | null}
     */
    function aim(hit) {
        const p = hit.point;
        try {
            return dotnet.invokeMethod('Aim', p.x + W / 2, p.y / PH, p.z + D / 2, hit.partId);
        } catch (err) {
            console.error('[BrickForge] Aim failed', err);
            return null;
        }
    }

    // ---- input ---------------------------------------------------------------------------
    // C# decides what a click or key means (place, paint, eyedrop, pick up, undo...);
    // this side only reports where the pointer is and which modifiers are held.
    const PLAIN_KEYS = new Set(['r', 'b', 'p', 's', 'e', 'escape', 'delete', 'backspace']);
    const CTRL_KEYS = new Set(['z', 'y', 'c', 'x', 'v', 'a']);

    /** @type {{ x: number, y: number, button: number } | null} */
    let press = null;

    // Box-select: in select mode, a left-drag draws a rectangle; parts whose centres fall inside it
    // (including ones hidden behind others, as in most 3D editors) are selected on release.
    /** @type {HTMLDivElement | null} */
    let boxSelect = null;

    /** @param {PointerEvent} e */
    function updateBoxSelect(e) {
        if (!press || press.button !== 0 || tool?.mode !== 'select') return;
        if (!boxSelect) {
            if (Math.hypot(e.clientX - press.x, e.clientY - press.y) <= CLICK_TOLERANCE_PX) return;
            boxSelect = document.createElement('div');
            boxSelect.className = 'select-box';
            host.appendChild(boxSelect);
            refreshHover(); // hide the hover outline while dragging
        }
        const rect = host.getBoundingClientRect();
        const left = Math.min(press.x, e.clientX) - rect.left, top = Math.min(press.y, e.clientY) - rect.top;
        Object.assign(boxSelect.style, {
            left: `${left}px`, top: `${top}px`,
            width: `${Math.abs(e.clientX - press.x)}px`, height: `${Math.abs(e.clientY - press.y)}px`,
        });
    }

    /** Ids of parts whose centre projects inside the box-select rectangle. */
    function partsInBox() {
        const box = /** @type {HTMLDivElement} */ (boxSelect).getBoundingClientRect();
        const rect = canvas.getBoundingClientRect();
        const ids = [];
        const v = new THREE.Vector3();
        for (const rec of parts.records.values()) {
            v.set(rec.x + rec.sx / 2 - W / 2, (rec.y + rec.h / 2) * PH, rec.z + rec.sz / 2 - D / 2).project(camera);
            if (v.z > 1) continue; // behind the camera
            const sx = rect.left + (v.x + 1) / 2 * rect.width, sy = rect.top + (1 - v.y) / 2 * rect.height;
            if (sx >= box.left && sx <= box.right && sy >= box.top && sy <= box.bottom) ids.push(rec.id);
        }
        return ids;
    }

    /** @param {PointerEvent} e */
    function onPointerMove(e) {
        const rect = canvas.getBoundingClientRect();
        pointer.set(((e.clientX - rect.left) / rect.width) * 2 - 1, -((e.clientY - rect.top) / rect.height) * 2 + 1);
        pointerInside = true;
        setModifiers(e);
        updateBoxSelect(e);
        updateHover();
    }

    function onPointerLeave() {
        pointerInside = false;
        updateHover();
    }

    /** @param {PointerEvent} e */
    function onPointerDown(e) {
        press = { x: e.clientX, y: e.clientY, button: e.button };
    }

    /** @param {PointerEvent} e */
    async function onPointerUp(e) {
        const p = press;
        press = null;
        if (!p || p.button !== e.button) return;
        const ctrl = e.ctrlKey || e.metaKey;

        if (boxSelect) {
            const ids = partsInBox();
            boxSelect.remove();
            boxSelect = null;
            refreshHover();
            await call('BoxSelect', ids, ctrl || e.shiftKey);
            return;
        }
        if (Math.hypot(e.clientX - p.x, e.clientY - p.y) > CLICK_TOLERANCE_PX) return; // was a drag

        if (e.button === 0) {
            await call('Click', candidate, hoveredPartId, e.altKey, e.shiftKey, ctrl);
        } else if (e.button === 2 && hoveredPartId !== null) {
            await call('Remove', hoveredPartId);
        }
    }

    /** @param {KeyboardEvent} e */
    async function onKeyDown(e) {
        if (isTyping(e)) return;
        setModifiers(e);
        const key = e.key.toLowerCase();
        const ctrl = e.ctrlKey || e.metaKey;
        if (key === 'f' && !ctrl) {
            resetView(); // camera only, so C# needn't know
            return;
        }
        if (!(ctrl ? CTRL_KEYS.has(key) : PLAIN_KEYS.has(key))) return;
        e.preventDefault();
        await call('Key', key, ctrl, e.shiftKey, hoveredPartId);
    }

    /** @param {KeyboardEvent} e */
    function onKeyUp(e) {
        setModifiers(e);
        if (e.key === 'Alt') e.preventDefault(); // stop Windows focusing the browser menu
    }

    function onBlur() {
        setModifiers({ altKey: false, shiftKey: false });
    }

    /** @param {{ altKey: boolean, shiftKey: boolean }} e */
    function setModifiers(e) {
        if (modifiers.alt === e.altKey && modifiers.shift === e.shiftKey) return;
        modifiers.alt = e.altKey;
        modifiers.shift = e.shiftKey;
        refreshHover();
    }

    function resetView() {
        camera.position.copy(homePosition);
        controls.target.set(0, 0, 0);
        controls.update();
    }

    /** Re-evaluate what's under a stationary cursor after the scene or tool changed. */
    function refreshHover() {
        candidate = null;
        hoverSeq++;
        updateHover();
    }

    // Drag and drop from the panel. Tiles marked data-brickforge-drag start a native drag; C# has
    // already made the dragged part or component the ghost (via the tile's @ondragstart), so over the
    // canvas this behaves like hovering, and dropping behaves like a click.
    let panelDrag = false;

    /** @param {DragEvent} e */
    function onDocumentDragStart(e) {
        const tile = e.target instanceof Element ? e.target.closest('[data-brickforge-drag]') : null;
        if (!tile || !e.dataTransfer) return;
        panelDrag = true;
        e.dataTransfer.setData('text/plain', 'brickforge'); // Firefox won't start a drag without data
        e.dataTransfer.effectAllowed = 'copy';
    }

    /** @param {DragEvent} e */
    function onDragOver(e) {
        if (!panelDrag) return; // not ours: leave the default (no drop)
        e.preventDefault();
        if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
        const rect = canvas.getBoundingClientRect();
        pointer.set(((e.clientX - rect.left) / rect.width) * 2 - 1, -((e.clientY - rect.top) / rect.height) * 2 + 1);
        pointerInside = true;
        updateHover();
    }

    function onDragLeave() {
        pointerInside = false;
        updateHover();
    }

    /** @param {DragEvent} e */
    async function onDrop(e) {
        if (!panelDrag) return;
        e.preventDefault();
        if (candidate) await call('Drop', candidate);
    }

    async function onDocumentDragEnd() {
        if (!panelDrag) return;
        panelDrag = false;
        await call('DragEnded');
    }

    canvas.addEventListener('dragover', onDragOver);
    canvas.addEventListener('dragleave', onDragLeave);
    canvas.addEventListener('drop', onDrop);
    document.addEventListener('dragstart', onDocumentDragStart);
    document.addEventListener('dragend', onDocumentDragEnd);
    canvas.addEventListener('pointermove', onPointerMove);
    canvas.addEventListener('pointerleave', onPointerLeave);
    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointerup', onPointerUp);
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    window.addEventListener('blur', onBlur);
    controls.addEventListener('change', () => { if (pointerInside) refreshHover(); });

    // ---- lifecycle -----------------------------------------------------------------------
    const resize = () => {
        const { clientWidth: w, clientHeight: h } = host;
        if (w === 0 || h === 0) return;
        renderer.setSize(w, h, false);
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
    };
    const resizeObserver = new ResizeObserver(resize);
    resizeObserver.observe(host);
    resize();

    renderer.setAnimationLoop(() => {
        controls.update();
        renderer.render(scene, camera);
    });

    // Component thumbnails come from a small second renderer, so they never disturb the main view.
    /** @type {{ renderer: THREE.WebGLRenderer, scene: THREE.Scene, camera: THREE.PerspectiveCamera } | null} */
    let thumbnails = null;

    /**
     * Renders a group (flat, as for the ghost) to a PNG data URL, framed to fit.
     * @param {number[]} flat @param {[number, number, number]} size @param {number} width @param {number} height
     */
    function renderThumbnail(flat, size, width, height) {
        if (!thumbnails) {
            const r = new THREE.WebGLRenderer({ antialias: true, alpha: true, preserveDrawingBuffer: true });
            r.toneMapping = THREE.NeutralToneMapping;
            const s = new THREE.Scene();
            s.add(new THREE.HemisphereLight(0xffffff, 0x445066, 1.4));
            const light = new THREE.DirectionalLight(0xffffff, 2.2);
            light.position.set(3, 5, 2);
            s.add(light);
            thumbnails = { renderer: r, scene: s, camera: new THREE.PerspectiveCamera(30, 1, 0.1, 1000) };
        }
        const { renderer: r, scene: s, camera: cam } = thumbnails;
        r.setSize(width, height, false);
        cam.aspect = width / height;

        const group = new THREE.Group();
        for (let i = 0; i < flat.length; i += 6) {
            const def = o.parts[flat[i]];
            const mesh = new THREE.Mesh(geometries.model(def.id), materials.get(flat[i + 5]));
            const [sx, sz] = footprint(def, flat[i + 4]);
            mesh.position.set(flat[i + 1] + sx / 2, flat[i + 2] * PH, flat[i + 3] + sz / 2);
            mesh.rotation.y = -flat[i + 4] * Math.PI / 2;
            group.add(mesh);
        }
        const [gx, gy, gz] = size;
        const centre = new THREE.Vector3(gx / 2, (gy * PH + o.studHeight) / 2, gz / 2);
        const radius = new THREE.Vector3(gx, gy * PH + o.studHeight, gz).length() / 2;
        const fov = THREE.MathUtils.degToRad(cam.fov) / 2;
        const distance = radius / Math.sin(Math.min(fov, Math.atan(Math.tan(fov) * cam.aspect))) * 1.05;
        cam.position.copy(centre).add(new THREE.Vector3(1, 0.85, 1.25).normalize().multiplyScalar(distance));
        cam.lookAt(centre);
        cam.updateProjectionMatrix();

        s.add(group);
        r.render(s, cam);
        s.remove(group);
        return r.domElement.toDataURL('image/png');
    }

    const api = {
        /**
         * @param {number[]} flat [catalogIndex, dx, dy, dz, rotation, colorId] per part
         * @param {[number, number, number]} size @param {number} width @param {number} height
         */
        renderThumbnail(flat, size, width, height) {
            return renderThumbnail(flat, size, width, height);
        },
        /**
         * Applies one change from C#: removals first, then additions, then the current tool.
         * `added` is flat: [id, catalogIndex, x, y, z, rotation, colorId] per part.
         * `baseplate` is present only when the baseplate size changed.
         * @param {{ added: number[], removed: number[], tool: Tool, baseplate?: { width: number, depth: number } }} update
         */
        update({ added, removed, tool: next, baseplate: plate }) {
            if (plate) setBaseplate(plate.width, plate.depth); // drops every part, so no removals needed
            else for (const id of removed) parts.remove(id);
            for (let i = 0; i < added.length; i += 7) {
                parts.add({
                    id: added[i], partId: o.parts[added[i + 1]].id,
                    x: added[i + 2], y: added[i + 3], z: added[i + 4], rotation: added[i + 5], colorId: added[i + 6],
                });
            }
            const ghostChanged = !tool || !sameNumbers(tool.ghost, next.ghost);
            tool = next;
            if (ghostChanged) rebuildGhost();
            updateSelection();
            refreshHover();
        },
        /** Rendering counters, for checking performance from the console. */
        stats() {
            return {
                parts: parts.records.size,
                visibleStuds: parts.studs.size,
                partTriangles: parts.triangleCount(),
                drawCalls: renderer.info.render.calls,
                frameTriangles: renderer.info.render.triangles,
                hoveredPartId,
                candidate,
            };
        },
        /**
         * Average milliseconds to render one frame, measured synchronously (so it works even when the
         * tab is hidden and requestAnimationFrame is paused). A 1-pixel read-back forces the GPU to finish.
         * @param {number} frames
         */
        benchmark(frames = 30) {
            const gl = renderer.getContext();
            const pixel = new Uint8Array(4);
            renderer.render(scene, camera); // warm-up: shader compilation
            gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
            const start = performance.now();
            for (let i = 0; i < frames; i++) {
                renderer.render(scene, camera);
                gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
            }
            return +((performance.now() - start) / frames).toFixed(2);
        },
        dispose() {
            renderer.setAnimationLoop(null);
            resizeObserver.disconnect();
            window.removeEventListener('keydown', onKeyDown);
            window.removeEventListener('keyup', onKeyUp);
            window.removeEventListener('blur', onBlur);
            document.removeEventListener('dragstart', onDocumentDragStart);
            document.removeEventListener('dragend', onDocumentDragEnd);
            thumbnails?.renderer.dispose();
            controls.dispose();
            scene.traverse(obj => {
                if (obj instanceof THREE.Mesh || obj instanceof THREE.LineSegments) obj.geometry.dispose();
            });
            parts.dispose();
            geometries.dispose();
            materials.dispose();
            baseplate.material.dispose();
            ghostLine.dispose();
            guideMaterial.dispose();
            ghostInvalid.dispose();
            ghostMaterials.forEach(m => m.dispose());
            selectionBatch?.dispose();
            selectionShell.dispose();
            selectionMaterial.dispose();
            boxSelect?.remove();
            highlightMaterial.dispose();
            renderer.dispose();
            canvas.remove();
        },
    };
    // Reachable from the console as document.querySelector('.viewport').viewport, e.g. for .stats().
    Object.assign(host, { viewport: api });
    return api;
}

// ---- parts rendering ---------------------------------------------------------------------

const STUD_SEGMENTS = 12;

/**
 * Per part type: a plain body box for instancing (studs are drawn separately), and a full
 * body-plus-studs model for the ghost preview. Plus the one shared stud geometry.
 */
class PartGeometries {
    /** @param {ViewportOptions} o @param {Map<string, PartDef>} defs */
    constructor(o, defs) {
        this.o = o;
        this.defs = defs;
        /** @type {Map<string, THREE.BufferGeometry>} */
        this.bodies = new Map();
        /** @type {Map<string, THREE.BufferGeometry>} */
        this.models = new Map();
        this.stud = createStudGeometry(o);
    }

    /** Centred on x/z, bottom at y = 0, unrotated. @param {string} partId */
    body(partId) {
        let g = this.bodies.get(partId);
        if (!g) {
            const def = /** @type {PartDef} */ (this.defs.get(partId));
            const h = def.heightPlates * this.o.plateHeight, inset = this.o.partInset;
            g = new THREE.BoxGeometry(def.width - inset * 2, h, def.depth - inset * 2);
            g.translate(0, h / 2, 0);
            this.bodies.set(partId, g);
        }
        return g;
    }

    /** @param {string} partId */
    model(partId) {
        let g = this.models.get(partId);
        if (!g) {
            const def = /** @type {PartDef} */ (this.defs.get(partId));
            const h = def.heightPlates * this.o.plateHeight;
            const pieces = [this.body(partId).clone()];
            if (def.hasStuds) {
                for (let i = 0; i < def.width; i++) {
                    for (let j = 0; j < def.depth; j++) {
                        pieces.push(this.stud.clone().translate(i + 0.5 - def.width / 2, h, j + 0.5 - def.depth / 2));
                    }
                }
            }
            g = /** @type {THREE.BufferGeometry} */ (mergeGeometries(pieces));
            pieces.forEach(p => p.dispose());
            this.models.set(partId, g);
        }
        return g;
    }

    dispose() {
        [...this.bodies.values(), ...this.models.values(), this.stud].forEach(g => g.dispose());
    }
}

/** An open cylinder with a top cap only: the bottom is always hidden inside the part. @param {ViewportOptions} o */
function createStudGeometry(o) {
    const r = o.studDiameter / 2;
    const side = new THREE.CylinderGeometry(r, r, o.studHeight, STUD_SEGMENTS, 1, true);
    side.translate(0, o.studHeight / 2, 0);
    const cap = new THREE.CircleGeometry(r, STUD_SEGMENTS);
    cap.rotateX(-Math.PI / 2);
    cap.translate(0, o.studHeight, 0);
    const stud = /** @type {THREE.BufferGeometry} */ (mergeGeometries([side, cap]));
    side.dispose();
    cap.dispose();
    return stud;
}

class ColorMaterials {
    /** @param {ColorDef[]} colors */
    constructor(colors) {
        /** @type {Map<number, THREE.MeshStandardMaterial>} */
        this.byId = new Map(colors.map(c => [c.id, new THREE.MeshStandardMaterial({
            color: new THREE.Color(c.hex),
            roughness: 0.3,
            transparent: c.isTransparent,
            opacity: c.isTransparent ? 0.55 : 1,
            depthWrite: !c.isTransparent,
        })]));
    }

    /** @param {number} id */
    get(id) { return this.byId.get(id); }

    dispose() { this.byId.forEach(m => m.dispose()); }
}

/**
 * A growable InstancedMesh whose instances are addressed by key.
 * Removal swaps the last instance into the freed slot, so draws stay contiguous.
 * @template K
 */
class InstanceBatch {
    /**
     * @param {THREE.Scene} scene @param {THREE.BufferGeometry} geometry
     * @param {THREE.Material} material @param {boolean} castShadow
     */
    constructor(scene, geometry, material, castShadow) {
        this.scene = scene;
        this.geometry = geometry;
        this.material = material;
        this.castShadow = castShadow;
        /** @type {K[]} */
        this.keys = [];
        /** @type {Map<K, number>} */
        this.indexOf = new Map();
        this.mesh = this.createMesh(16);
    }

    /** @param {number} capacity */
    createMesh(capacity) {
        const mesh = new THREE.InstancedMesh(this.geometry, this.material, capacity);
        mesh.castShadow = this.castShadow;
        mesh.receiveShadow = true;
        mesh.count = this.keys.length;
        this.scene.add(mesh);
        return mesh;
    }

    /** @param {K} key @param {THREE.Matrix4} matrix */
    add(key, matrix) {
        if (this.keys.length === this.mesh.instanceMatrix.count) this.grow();
        const index = this.keys.length;
        this.keys.push(key);
        this.indexOf.set(key, index);
        this.mesh.setMatrixAt(index, matrix);
        this.mesh.count = this.keys.length;
        this.touched();
    }

    /** @param {K} key */
    remove(key) {
        const index = this.indexOf.get(key);
        if (index === undefined) return;
        const last = this.keys.length - 1;
        if (index !== last) {
            this.mesh.getMatrixAt(last, scratchMatrix);
            this.mesh.setMatrixAt(index, scratchMatrix);
            const moved = this.keys[last];
            this.keys[index] = moved;
            this.indexOf.set(moved, index);
        }
        this.keys.pop();
        this.indexOf.delete(key);
        this.mesh.count = this.keys.length;
        this.touched();
    }

    /** @param {number} index */
    keyAt(index) { return this.keys[index]; }

    grow() {
        const old = this.mesh;
        this.mesh = this.createMesh(old.instanceMatrix.count * 2);
        this.mesh.instanceMatrix.array.set(old.instanceMatrix.array);
        this.scene.remove(old);
        old.dispose();
    }

    touched() {
        this.mesh.instanceMatrix.needsUpdate = true;
        this.mesh.boundingSphere = null; // recomputed lazily for raycasting/culling
        this.mesh.boundingBox = null;
    }

    dispose() {
        this.scene.remove(this.mesh);
        this.mesh.dispose();
    }
}

const scratchMatrix = new THREE.Matrix4();
const scratchObject = new THREE.Object3D();

/**
 * @typedef {{ id: number, bodyKey: string, x: number, y: number, z: number, sx: number, sz: number,
 *             h: number, hasStuds: boolean, colorId: number }} PartRecord
 */

/**
 * Draws the placed parts. Bodies are batched per (part type, colour) and cast shadows.
 * Studs are batched per colour and drawn only where exposed — where the cell above is empty —
 * which hides most of them on a real build. The baseplate's studs are managed the same way.
 */
class PartRenderer {
    /**
     * @param {THREE.Scene} scene @param {PartGeometries} geometries @param {ColorMaterials} materials
     * @param {ViewportOptions} o @param {THREE.Material} baseplateMaterial
     */
    constructor(scene, geometries, materials, o, baseplateMaterial) {
        this.scene = scene;
        this.geometries = geometries;
        this.materials = materials;
        this.o = o;
        this.baseplateMaterial = baseplateMaterial;
        /** @type {Map<number, PartRecord>} */
        this.records = new Map();
        /** Occupied grid cells "x,y,z" → part id. @type {Map<string, number>} */
        this.cells = new Map();
        /** @type {Map<string, InstanceBatch<number>>} */
        this.bodyBatches = new Map();
        /** Keyed by colour id, or 'base' for the baseplate. @type {Map<number | 'base', InstanceBatch<string>>} */
        this.studBatches = new Map();
        /** Visible stud at "x,y,z" (y = the level it stands on) → its batch key. @type {Map<string, number | 'base'>} */
        this.studs = new Map();

        for (let x = 0; x < o.baseplateWidth; x++) {
            for (let z = 0; z < o.baseplateDepth; z++) this.addStud(x, 0, z, 'base');
        }
    }

    meshes() { return [...this.bodyBatches.values()].map(b => b.mesh); }

    /** @param {number} id */
    record(id) { return this.records.get(id); }

    /** @param {THREE.Object3D} mesh @param {number} index @returns {number | null} */
    partIdAt(mesh, index) {
        for (const batch of this.bodyBatches.values()) {
            if (batch.mesh === mesh) return batch.keyAt(index) ?? null;
        }
        return null;
    }

    /** @param {PlacedDto} dto */
    add(dto) {
        const def = this.geometries.defs.get(dto.partId);
        if (!def || this.records.has(dto.id)) return;
        const [sx, sz] = footprint(def, dto.rotation);
        /** @type {PartRecord} */
        const rec = {
            id: dto.id, bodyKey: `${dto.partId}|${dto.colorId}`, x: dto.x, y: dto.y, z: dto.z,
            sx, sz, h: def.heightPlates, hasStuds: def.hasStuds, colorId: dto.colorId,
        };
        this.records.set(rec.id, rec);

        placeObject(scratchObject, dto, sx, sz, dto.rotation, this.o);
        scratchObject.updateMatrix();
        this.bodyBatch(rec.bodyKey, dto.partId, dto.colorId).add(rec.id, scratchObject.matrix);

        for (let x = rec.x; x < rec.x + sx; x++) {
            for (let z = rec.z; z < rec.z + sz; z++) {
                for (let y = rec.y; y < rec.y + rec.h; y++) this.cells.set(cellKey(x, y, z), rec.id);
                this.removeStud(x, rec.y, z); // now pushed into our underside
                if (rec.hasStuds && !this.cells.has(cellKey(x, rec.y + rec.h, z))) {
                    this.addStud(x, rec.y + rec.h, z, rec.colorId);
                }
            }
        }
    }

    /** @param {number} id */
    remove(id) {
        const rec = this.records.get(id);
        if (!rec) return;
        this.records.delete(id);
        this.bodyBatches.get(rec.bodyKey)?.remove(id);

        for (let x = rec.x; x < rec.x + rec.sx; x++) {
            for (let z = rec.z; z < rec.z + rec.sz; z++) {
                for (let y = rec.y; y < rec.y + rec.h; y++) this.cells.delete(cellKey(x, y, z));
                this.removeStud(x, rec.y + rec.h, z);
                // Whatever we were sitting on has its studs uncovered.
                if (rec.y === 0) {
                    this.addStud(x, 0, z, 'base');
                } else {
                    const below = this.records.get(this.cells.get(cellKey(x, rec.y - 1, z)) ?? -1);
                    if (below?.hasStuds) this.addStud(x, rec.y, z, below.colorId);
                }
            }
        }
    }

    /** @param {number} x @param {number} y @param {number} z @param {number | 'base'} batchKey */
    addStud(x, y, z, batchKey) {
        const key = cellKey(x, y, z);
        if (this.studs.has(key)) return;
        let batch = this.studBatches.get(batchKey);
        if (!batch) {
            const material = batchKey === 'base' ? this.baseplateMaterial : this.materials.get(batchKey);
            batch = new InstanceBatch(this.scene, this.geometries.stud, /** @type {THREE.Material} */ (material), false);
            this.studBatches.set(batchKey, batch);
        }
        scratchMatrix.makeTranslation(
            x + 0.5 - this.o.baseplateWidth / 2, y * this.o.plateHeight, z + 0.5 - this.o.baseplateDepth / 2);
        batch.add(key, scratchMatrix);
        this.studs.set(key, batchKey);
    }

    /** @param {number} x @param {number} y @param {number} z */
    removeStud(x, y, z) {
        const key = cellKey(x, y, z);
        const batchKey = this.studs.get(key);
        if (batchKey === undefined) return;
        this.studBatches.get(batchKey)?.remove(key);
        this.studs.delete(key);
    }

    /** @param {string} key @param {string} partId @param {number} colorId */
    bodyBatch(key, partId, colorId) {
        let batch = this.bodyBatches.get(key);
        if (!batch) {
            const material = /** @type {THREE.Material} */ (this.materials.get(colorId));
            batch = new InstanceBatch(this.scene, this.geometries.body(partId), material, true);
            this.bodyBatches.set(key, batch);
        }
        return batch;
    }

    /** Triangles submitted per frame for parts and studs, for performance checks. */
    triangleCount() {
        let total = 0;
        for (const b of [...this.bodyBatches.values(), ...this.studBatches.values()]) {
            const index = b.geometry.index;
            total += ((index ? index.count : b.geometry.attributes.position.count) / 3) * b.mesh.count;
        }
        return total;
    }

    dispose() {
        [...this.bodyBatches.values(), ...this.studBatches.values()].forEach(b => b.dispose());
    }
}

/** @param {number} x @param {number} y @param {number} z */
function cellKey(x, y, z) { return `${x},${y},${z}`; }

// ---- helpers -----------------------------------------------------------------------------

/** @param {PartDef} def @param {number} rotation @returns {[number, number]} */
function footprint(def, rotation) {
    return rotation % 2 === 1 ? [def.depth, def.width] : [def.width, def.depth];
}

/**
 * Positions an object whose geometry is centred on its unrotated footprint.
 * @param {THREE.Object3D} obj @param {Cell} cell @param {number} sx @param {number} sz
 * @param {number} rotation @param {ViewportOptions} o
 */
function placeObject(obj, cell, sx, sz, rotation, o) {
    obj.position.set(
        cell.x + sx / 2 - o.baseplateWidth / 2,
        cell.y * o.plateHeight,
        cell.z + sz / 2 - o.baseplateDepth / 2);
    obj.rotation.set(0, -rotation * Math.PI / 2, 0);
}
/** @param {Cell | null} a @param {Cell | null} b */
function sameCell(a, b) {
    return a === b || (!!a && !!b && a.x === b.x && a.y === b.y && a.z === b.z);
}

/** @param {number[]} a @param {number[]} b */
function sameNumbers(a, b) {
    return a.length === b.length && a.every((v, i) => v === b[i]);
}

/** @param {KeyboardEvent} e */
function isTyping(e) {
    const t = /** @type {HTMLElement | null} */ (e.target);
    return !!t && (t.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName));
}

/**
 * Baseplate slab, its top surface at y = 0 and centred on the origin. Its studs are drawn by
 * PartRenderer, so the ones covered by parts can be hidden.
 * @param {ViewportOptions} o
 */
function createBaseplate(o) {
    const material = new THREE.MeshStandardMaterial({ color: 0x237841, roughness: 0.45 });
    const thickness = o.plateHeight / 2;
    const slab = new THREE.Mesh(new THREE.BoxGeometry(o.baseplateWidth, thickness, o.baseplateDepth), material);
    slab.position.y = -thickness / 2;
    slab.receiveShadow = true;
    return { slab, material };
}