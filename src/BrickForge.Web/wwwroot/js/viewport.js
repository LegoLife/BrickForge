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
 * @typedef {{ invokeMethodAsync(name: string, ...args: any[]): Promise<any> }} DotNetRef
 * @typedef {{ x: number, y: number, z: number }} Cell
 */

const CLICK_TOLERANCE_PX = 5;
const VALID_COLOR = 0x4ade80;
const INVALID_COLOR = 0xef4444;

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
    const W = o.baseplateWidth, D = o.baseplateDepth, PH = o.plateHeight;

    const renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(window.devicePixelRatio);
    renderer.shadowMap.enabled = true;
    host.appendChild(renderer.domElement);
    const canvas = renderer.domElement;

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x1d2330);

    const span = Math.max(W, D);
    const camera = new THREE.PerspectiveCamera(45, 1, 0.1, span * 20);
    camera.position.set(span * 0.7, span * 0.75, span * 1.0);

    const controls = new OrbitControls(camera, canvas);
    controls.enableDamping = true;
    controls.minDistance = 2;
    controls.maxDistance = span * 4;
    controls.maxPolarAngle = Math.PI * 0.49; // don't go under the baseplate
    // Left mouse is reserved for placing bricks; orbit on right, pan on middle.
    controls.mouseButtons = { LEFT: null, MIDDLE: THREE.MOUSE.PAN, RIGHT: THREE.MOUSE.ROTATE };
    controls.update();

    addLights(scene, span);
    const baseplate = createBaseplate(o);
    scene.add(baseplate);

    const geometries = new PartGeometries(o, partDefs);
    const materials = new ColorMaterials(o.colors);
    const parts = new PartBatches(scene, geometries, materials, o);

    // ---- tool, ghost & highlight -----------------------------------------------------------
    /** @typedef {{ partId: string, colorId: number, rotation: number, mode: 'build' | 'paint' }} Tool */
    /** @type {Tool | null} */
    let tool = null;
    const ghostBody = new THREE.MeshStandardMaterial({ transparent: true, opacity: 0.55, depthWrite: false });
    const ghostLine = new THREE.LineBasicMaterial({ color: VALID_COLOR });
    const ghost = new THREE.Group();
    ghost.visible = false;
    scene.add(ghost);

    function rebuildGhost() {
        ghost.children.forEach(c => { if (c instanceof THREE.LineSegments) c.geometry.dispose(); });
        ghost.clear();
        if (!tool) return;
        const def = partDefs.get(tool.partId);
        if (!def) return;
        ghost.add(new THREE.Mesh(geometries.get(def.id), ghostBody));
        const box = new THREE.BoxGeometry(def.width, def.heightPlates * PH, def.depth);
        box.translate(0, def.heightPlates * PH / 2, 0);
        ghost.add(new THREE.LineSegments(new THREE.EdgesGeometry(box), ghostLine));
        box.dispose();
    }

    /** @param {boolean} valid */
    function paintGhost(valid) {
        const color = tool && materials.get(tool.colorId);
        if (valid && color) ghostBody.color.copy(color.color);
        else ghostBody.color.setHex(INVALID_COLOR);
        ghostBody.opacity = valid ? 0.6 : 0.35;
        ghostLine.color.setHex(valid ? VALID_COLOR : INVALID_COLOR);
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
    const targetingPart = () => tool?.mode === 'paint' || modifiers.alt || modifiers.shift;

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

        const def = tool?.mode === 'build' ? partDefs.get(tool.partId) : undefined;
        const aimingAtPart = hoveredPartId !== null && (modifiers.alt || modifiers.shift);
        const next = hit && def && tool && !aimingAtPart ? candidateFor(hit, def, tool.rotation) : null;
        if (sameCell(next, candidate)) return;

        candidate = next;
        if (!candidate || !def || !tool) {
            ghost.visible = false;
            return;
        }
        const [sx, sz] = footprint(def, tool.rotation);
        placeObject(ghost, candidate, sx, sz, tool.rotation, o);
        ghost.visible = true;

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
        const hits = raycaster.intersectObjects([...parts.meshes(), baseplate], true);
        const first = hits[0];
        if (!first) return null;
        const partId = first.object instanceof THREE.InstancedMesh && first.instanceId !== undefined
            ? parts.partIdAt(first.object, first.instanceId)
            : null;
        return { point: first.point, partId };
    }

    /**
     * Where the selected part would go, as its minimum corner, given what the cursor is over.
     * @param {Hit} hit @param {PartDef} def @param {number} rotation
     * @returns {Cell}
     */
    function candidateFor(hit, def, rotation) {
        const p = hit.point;
        let cx = Math.floor(p.x + W / 2), cz = Math.floor(p.z + D / 2), y = 0;

        const target = hit.partId !== null ? parts.record(hit.partId) : null;
        if (target) {
            const eps = 1e-3;
            const bottom = target.y * PH, top = (target.y + target.h) * PH;
            if (p.y >= top - eps) {
                y = target.y + target.h;                 // on top (body or studs)
            } else if (p.y <= bottom + eps) {
                y = target.y - def.heightPlates;          // underneath
            } else {
                // Side face: step out of the target through the nearest side, same base level.
                y = target.y;
                const gx = p.x + W / 2, gz = p.z + D / 2;
                const sides = [
                    [gx - target.x, -1, 0],
                    [target.x + target.sx - gx, 1, 0],
                    [gz - target.z, 0, -1],
                    [target.z + target.sz - gz, 0, 1],
                ].sort((a, b) => a[0] - b[0]);
                const [, dx, dz] = sides[0];
                cx = clamp(Math.floor(gx), target.x, target.x + target.sx - 1) + dx;
                cz = clamp(Math.floor(gz), target.z, target.z + target.sz - 1) + dz;
            }
        }

        const [sx, sz] = footprint(def, rotation);
        return {
            x: clamp(cx - Math.floor((sx - 1) / 2), 0, W - sx),
            y,
            z: clamp(cz - Math.floor((sz - 1) / 2), 0, D - sz),
        };
    }

    // ---- input ---------------------------------------------------------------------------
    // C# decides what a click or key means (place, paint, eyedrop, pick up, undo...);
    // this side only reports where the pointer is and which modifiers are held.
    const PLAIN_KEYS = new Set(['r', 'b', 'p', 'e', 'escape', 'delete', 'backspace']);
    const CTRL_KEYS = new Set(['z', 'y']);

    /** @type {{ x: number, y: number, button: number } | null} */
    let press = null;

    /** @param {PointerEvent} e */
    function onPointerMove(e) {
        const rect = canvas.getBoundingClientRect();
        pointer.set(((e.clientX - rect.left) / rect.width) * 2 - 1, -((e.clientY - rect.top) / rect.height) * 2 + 1);
        pointerInside = true;
        setModifiers(e);
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
        if (Math.hypot(e.clientX - p.x, e.clientY - p.y) > CLICK_TOLERANCE_PX) return; // was a drag

        if (e.button === 0) {
            await call('Click', candidate, hoveredPartId, e.altKey, e.shiftKey);
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

    /** Re-evaluate what's under a stationary cursor after the scene or tool changed. */
    function refreshHover() {
        candidate = null;
        hoverSeq++;
        updateHover();
    }

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

    return {
        /**
         * Applies one change from C#: removals first, then additions, then the current tool.
         * @param {{ added: PlacedDto[], removed: number[], tool: Tool }} update
         */
        update({ added, removed, tool: next }) {
            for (const id of removed) parts.remove(id);
            for (const dto of added) parts.add(dto);
            const shapeChanged = !tool || tool.partId !== next.partId || tool.rotation !== next.rotation;
            tool = next;
            if (shapeChanged) rebuildGhost();
            refreshHover();
        },
        dispose() {
            renderer.setAnimationLoop(null);
            resizeObserver.disconnect();
            window.removeEventListener('keydown', onKeyDown);
            window.removeEventListener('keyup', onKeyUp);
            window.removeEventListener('blur', onBlur);
            controls.dispose();
            scene.traverse(obj => {
                if (obj instanceof THREE.Mesh || obj instanceof THREE.LineSegments) obj.geometry.dispose();
            });
            geometries.dispose();
            materials.dispose();
            ghostBody.dispose();
            ghostLine.dispose();
            highlightMaterial.dispose();
            renderer.dispose();
            canvas.remove();
        },
    };
}

// ---- parts rendering ---------------------------------------------------------------------

/** One merged body+studs geometry per part type, built lazily and shared by every instance. */
class PartGeometries {
    /** @param {ViewportOptions} o @param {Map<string, PartDef>} defs */
    constructor(o, defs) {
        this.o = o;
        this.defs = defs;
        /** @type {Map<string, THREE.BufferGeometry>} */
        this.cache = new Map();
    }

    /** @param {string} partId */
    get(partId) {
        let g = this.cache.get(partId);
        if (!g) {
            g = this.build(/** @type {PartDef} */ (this.defs.get(partId)));
            this.cache.set(partId, g);
        }
        return g;
    }

    /** Centred on x/z, bottom at y = 0, unrotated. @param {PartDef} def */
    build(def) {
        const { plateHeight: PH, partInset: inset, studDiameter, studHeight } = this.o;
        const h = def.heightPlates * PH;
        const pieces = [];

        const body = new THREE.BoxGeometry(def.width - inset * 2, h, def.depth - inset * 2);
        body.translate(0, h / 2, 0);
        pieces.push(body);

        if (def.hasStuds) {
            for (let i = 0; i < def.width; i++) {
                for (let j = 0; j < def.depth; j++) {
                    const stud = new THREE.CylinderGeometry(studDiameter / 2, studDiameter / 2, studHeight, 16);
                    stud.translate(i + 0.5 - def.width / 2, h + studHeight / 2, j + 0.5 - def.depth / 2);
                    pieces.push(stud);
                }
            }
        }

        const merged = /** @type {THREE.BufferGeometry} */ (mergeGeometries(pieces));
        pieces.forEach(p => p.dispose());
        return merged;
    }

    dispose() {
        this.cache.forEach(g => g.dispose());
        this.cache.clear();
    }
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
 * Parts are drawn as one InstancedMesh per (part type, colour), so thousands of parts cost a
 * handful of draw calls. Removal swaps the last instance into the freed slot.
 */
class PartBatches {
    /**
     * @param {THREE.Scene} scene @param {PartGeometries} geometries
     * @param {ColorMaterials} materials @param {ViewportOptions} o
     */
    constructor(scene, geometries, materials, o) {
        this.scene = scene;
        this.geometries = geometries;
        this.materials = materials;
        this.o = o;
        /** @type {Map<string, { mesh: THREE.InstancedMesh, ids: number[] }>} */
        this.batches = new Map();
        /** @typedef {{ key: string, x: number, y: number, z: number, sx: number, sz: number, h: number }} PartRecord */
        /** @type {Map<number, PartRecord>} */
        this.records = new Map();
        /** @type {Map<THREE.InstancedMesh, string>} */
        this.keyOfMesh = new Map();
    }

    meshes() { return [...this.keyOfMesh.keys()]; }

    /** @param {number} id */
    record(id) { return this.records.get(id); }

    /** @param {THREE.InstancedMesh} mesh @param {number} index */
    partIdAt(mesh, index) {
        const key = this.keyOfMesh.get(mesh);
        return key !== undefined ? this.batches.get(key)?.ids[index] ?? null : null;
    }

    /** @param {PlacedDto} dto */
    add(dto) {
        const def = this.geometries.defs.get(dto.partId);
        if (!def || this.records.has(dto.id)) return;
        const [sx, sz] = footprint(def, dto.rotation);
        const key = `${dto.partId}|${dto.colorId}`;
        const batch = this.ensureCapacity(key, dto);

        const matrix = new THREE.Matrix4();
        const holder = new THREE.Object3D();
        placeObject(holder, dto, sx, sz, dto.rotation, this.o);
        holder.updateMatrix();
        matrix.copy(holder.matrix);

        const index = batch.ids.length;
        batch.ids.push(dto.id);
        batch.mesh.setMatrixAt(index, matrix);
        batch.mesh.count = batch.ids.length;
        this.touched(batch.mesh);
        this.records.set(dto.id, { key, x: dto.x, y: dto.y, z: dto.z, sx, sz, h: def.heightPlates });
    }

    /** @param {number} id */
    remove(id) {
        const rec = this.records.get(id);
        if (!rec) return;
        this.records.delete(id);
        const batch = /** @type {{ mesh: THREE.InstancedMesh, ids: number[] }} */ (this.batches.get(rec.key));
        const index = batch.ids.indexOf(id);
        const last = batch.ids.length - 1;
        if (index !== last) {
            const m = new THREE.Matrix4();
            batch.mesh.getMatrixAt(last, m);
            batch.mesh.setMatrixAt(index, m);
            batch.ids[index] = batch.ids[last];
        }
        batch.ids.pop();
        batch.mesh.count = batch.ids.length;
        this.touched(batch.mesh);
    }

    /** @param {string} key @param {PlacedDto} dto */
    ensureCapacity(key, dto) {
        let batch = this.batches.get(key);
        const capacity = batch ? batch.mesh.instanceMatrix.count : 0;
        const needed = (batch?.ids.length ?? 0) + 1;
        if (batch && needed <= capacity) return batch;

        const material = /** @type {THREE.Material} */ (this.materials.get(dto.colorId));
        const mesh = new THREE.InstancedMesh(this.geometries.get(dto.partId), material, Math.max(16, capacity * 2));
        mesh.castShadow = true;
        mesh.receiveShadow = true;
        if (batch) {
            mesh.instanceMatrix.array.set(batch.mesh.instanceMatrix.array);
            this.scene.remove(batch.mesh);
            this.keyOfMesh.delete(batch.mesh);
            batch.mesh.dispose();
            batch.mesh = mesh;
        } else {
            batch = { mesh, ids: [] };
            this.batches.set(key, batch);
        }
        mesh.count = batch.ids.length;
        this.scene.add(mesh);
        this.keyOfMesh.set(mesh, key);
        return batch;
    }

    /** @param {THREE.InstancedMesh} mesh */
    touched(mesh) {
        mesh.instanceMatrix.needsUpdate = true;
        mesh.boundingSphere = null; // recomputed lazily for raycasting/culling
        mesh.boundingBox = null;
    }
}

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

/** @param {number} v @param {number} lo @param {number} hi */
function clamp(v, lo, hi) { return Math.min(Math.max(v, lo), hi); }

/** @param {KeyboardEvent} e */
function isTyping(e) {
    const t = /** @type {HTMLElement | null} */ (e.target);
    return !!t && (t.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName));
}

/** @param {THREE.Scene} scene @param {number} span */
function addLights(scene, span) {
    scene.add(new THREE.HemisphereLight(0xffffff, 0x445066, 1.2));

    const sun = new THREE.DirectionalLight(0xffffff, 2.2);
    sun.position.set(span * 0.4, span, span * 0.25);
    sun.castShadow = true;
    sun.shadow.mapSize.set(2048, 2048);
    const half = span * 0.6;
    Object.assign(sun.shadow.camera, { left: -half, right: half, top: half, bottom: -half, near: 1, far: span * 3 });
    sun.shadow.bias = -0.0005;
    scene.add(sun);
}

/**
 * Baseplate top surface sits at y = 0, centred on the origin. Studs are one InstancedMesh.
 * @param {ViewportOptions} o
 */
function createBaseplate(o) {
    const group = new THREE.Group();
    const material = new THREE.MeshStandardMaterial({ color: 0x237841, roughness: 0.45 });

    const thickness = o.plateHeight / 2;
    const slab = new THREE.Mesh(new THREE.BoxGeometry(o.baseplateWidth, thickness, o.baseplateDepth), material);
    slab.position.y = -thickness / 2;
    slab.receiveShadow = true;
    group.add(slab);

    const studGeometry = new THREE.CylinderGeometry(o.studDiameter / 2, o.studDiameter / 2, o.studHeight, 20);
    studGeometry.translate(0, o.studHeight / 2, 0);
    const studs = new THREE.InstancedMesh(studGeometry, material, o.baseplateWidth * o.baseplateDepth);
    studs.castShadow = true;
    studs.receiveShadow = true;

    const m = new THREE.Matrix4();
    let i = 0;
    for (let x = 0; x < o.baseplateWidth; x++) {
        for (let z = 0; z < o.baseplateDepth; z++) {
            m.setPosition(x + 0.5 - o.baseplateWidth / 2, 0, z + 0.5 - o.baseplateDepth / 2);
            studs.setMatrixAt(i++, m);
        }
    }
    group.add(studs);
    return group;
}
