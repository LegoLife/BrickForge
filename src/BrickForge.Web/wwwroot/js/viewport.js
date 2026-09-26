// @ts-check
// Three.js viewport. Owns rendering, camera and (later) hover/ghost/raycasting.
// C# owns the build model; this module only draws what it is told to.
import * as THREE from '../lib/three/three.module.js';
import { OrbitControls } from '../lib/three/addons/controls/OrbitControls.js';

/**
 * @typedef {object} ViewportOptions
 * @property {number} baseplateWidth  studs along x
 * @property {number} baseplateDepth  studs along z
 * @property {number} plateHeight     world units per plate
 * @property {number} studDiameter
 * @property {number} studHeight
 */

/**
 * @param {HTMLElement} host
 * @param {ViewportOptions} options
 */
export function createViewport(host, options) {
    const renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(window.devicePixelRatio);
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    host.appendChild(renderer.domElement);

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x1d2330);

    const span = Math.max(options.baseplateWidth, options.baseplateDepth);
    const camera = new THREE.PerspectiveCamera(45, 1, 0.1, span * 20);
    camera.position.set(span * 0.7, span * 0.75, span * 1.0);

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.target.set(0, 0, 0);
    controls.minDistance = 2;
    controls.maxDistance = span * 4;
    controls.maxPolarAngle = Math.PI * 0.49; // don't go under the baseplate
    // Left mouse is reserved for placing bricks; orbit on right, pan on middle.
    controls.mouseButtons = { LEFT: null, MIDDLE: THREE.MOUSE.PAN, RIGHT: THREE.MOUSE.ROTATE };
    controls.update();

    addLights(scene, span);
    scene.add(createBaseplate(options));

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
        dispose() {
            renderer.setAnimationLoop(null);
            resizeObserver.disconnect();
            controls.dispose();
            scene.traverse(obj => {
                if (obj instanceof THREE.Mesh) {
                    obj.geometry.dispose();
                    (Array.isArray(obj.material) ? obj.material : [obj.material]).forEach(m => m.dispose());
                }
            });
            renderer.dispose();
            renderer.domElement.remove();
        },
    };
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
