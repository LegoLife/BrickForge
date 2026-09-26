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
    renderer.toneMapping = THREE.NeutralToneMapping; // keeps brick colours saturated, unlike ACES
    host.appendChild(renderer.domElement);
    const canvas = renderer.domElement;

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x1d2330);

    const span = Math.max(W, D);
    const camera = new THREE.PerspectiveCamera(45, 1, 0.1, span * 20);
    const homePosition = new THREE.Vector3(span * 0.7, span * 0.75, span * 1.0);
    camera.position.copy(homePosition);

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
    scene.add(baseplate.slab);

    const geometries = new PartGeometries(o, partDefs);
    const materials = new ColorMaterials(o.colors);
    const parts = new PartRenderer(scene, geometries, materials, o, baseplate.material);

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
        ghost.add(new THREE.Mesh(geometries.model(def.id), ghostBody));
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
        const hits = raycaster.intersectObjects([...parts.meshes(), baseplate.slab], false);
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

    const api = {
        /**
         * Applies one change from C#: removals first, then additions, then the current tool.
         * `added` is flat: [id, catalogIndex, x, y, z, rotation, colorId] per part.
         * @param {{ added: number[], removed: number[], tool: Tool }} update
         */
        update({ added, removed, tool: next }) {
            for (const id of removed) parts.remove(id);
            for (let i = 0; i < added.length; i += 7) {
                parts.add({
                    id: added[i], partId: o.parts[added[i + 1]].id,
                    x: added[i + 2], y: added[i + 3], z: added[i + 4], rotation: added[i + 5], colorId: added[i + 6],
                });
            }
            const shapeChanged = !tool || tool.partId !== next.partId || tool.rotation !== next.rotation;
            tool = next;
            if (shapeChanged) rebuildGhost();
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
            controls.dispose();
            scene.traverse(obj => {
                if (obj instanceof THREE.Mesh || obj instanceof THREE.LineSegments) obj.geometry.dispose();
            });
            parts.dispose();
            geometries.dispose();
            materials.dispose();
            baseplate.material.dispose();
            ghostBody.dispose();
            ghostLine.dispose();
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