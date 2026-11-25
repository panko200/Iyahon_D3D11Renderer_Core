# -*- coding: utf-8 -*-
bl_info = {
    "name": "YMM4 D3D11 Auto-Bake GLB Exporter",
    "author": "Iyahon_D3D11Renderer_Core",
    "version": (1, 2, 0),
    "blender": (3, 0, 0),
    "location": "3D Viewport > Sidebar (N) > YMM4 D3D11 / File > Export > YMM4 D3D11 (.glb)",
    "description": "Blenderのプロシージャル・ノードマテリアルやUV未展開モデルを自動複製・UV展開・Cyclesベイクし、元のシーンを壊さずにYMM4(Iyahon_D3D11Renderer_Core)用GLBとして書き出します。",
    "category": "Import-Export",
}

import math
import os
import bpy
import bmesh
import numpy as np
from bpy.props import (
    BoolProperty,
    EnumProperty,
    FloatProperty,
    IntProperty,
    PointerProperty,
    StringProperty,
)
from bpy_extras.io_utils import ExportHelper


SUPPORTED_OBJECT_TYPES = {"MESH", "CURVE", "SURFACE", "FONT", "META"}

SHADER_TO_PBR_SPEC = {
    "BSDF_PRINCIPLED": "PRINCIPLED",
    "BSDF_DIFFUSE": "DIFFUSE_LIKE",
    "BSDF_TOON": "DIFFUSE_LIKE",
    "BSDF_VELVET": "DIFFUSE_LIKE",
    "BSDF_SHEEN": "DIFFUSE_LIKE",
    "BSDF_TRANSLUCENT": "DIFFUSE_LIKE",
    "SUBSURFACE_SCATTERING": "DIFFUSE_LIKE",
    "BSDF_GLOSSY": "GLOSSY_LIKE",
    "BSDF_ANISOTROPIC": "GLOSSY_LIKE",
    "BSDF_GLASS": "GLASS_LIKE",
    "BSDF_REFRACTION": "GLASS_LIKE",
    "BSDF_TRANSPARENT": "TRANSPARENT",
    "EMISSION": "EMISSION",
}


class TempResourceTracker:
    """ベイク中に生成した一時データブロックを追跡し、終了時に必ず破棄する。"""

    def __init__(self):
        self.objects = []
        self.collections = []
        self.meshes = []
        self.materials = []
        self.node_groups = []
        self.images = []

    def cleanup(self):
        for obj in reversed(self.objects):
            try:
                if obj and obj.name in bpy.data.objects:
                    bpy.data.objects.remove(obj, do_unlink=True)
            except Exception:
                pass
        self.objects.clear()

        for col in reversed(self.collections):
            try:
                if col and col.name in bpy.data.collections:
                    for scene in bpy.data.scenes:
                        if col.name in scene.collection.children:
                            scene.collection.children.unlink(col)
                    bpy.data.collections.remove(col, do_unlink=True)
            except Exception:
                pass
        self.collections.clear()

        for mesh in reversed(self.meshes):
            try:
                if mesh and mesh.name in bpy.data.meshes:
                    bpy.data.meshes.remove(mesh, do_unlink=True)
            except Exception:
                pass
        self.meshes.clear()

        for mat in reversed(self.materials):
            try:
                if mat and mat.name in bpy.data.materials:
                    bpy.data.materials.remove(mat, do_unlink=True)
            except Exception:
                pass
        self.materials.clear()

        for ng in reversed(self.node_groups):
            try:
                if ng and ng.name in bpy.data.node_groups:
                    bpy.data.node_groups.remove(ng, do_unlink=True)
            except Exception:
                pass
        self.node_groups.clear()

        for img in reversed(self.images):
            try:
                if img and img.name in bpy.data.images:
                    bpy.data.images.remove(img, do_unlink=True)
            except Exception:
                pass
        self.images.clear()


class SceneSnapshot:
    """レンダーエンジン・Cycles・ベイク設定・選択状態・モードを保存し復元する。"""

    def __init__(self, context):
        self.context = context
        self.scene = context.scene
        self.prev_active = context.view_layer.objects.active
        self.prev_selected = [o for o in context.selected_objects]
        self.prev_mode = self.prev_active.mode if self.prev_active else "OBJECT"
        self.prev_engine = self.scene.render.engine
        self.cycles_was_enabled = ("cycles" in bpy.context.preferences.addons)
        self.cycles_attrs = {}
        self.bake_attrs = {}

        if hasattr(self.scene, "cycles"):
            c = self.scene.cycles
            for k in (
                "device",
                "samples",
                "use_adaptive_sampling",
                "use_denoising",
                "max_bounces",
                "diffuse_bounces",
                "glossy_bounces",
                "transmission_bounces",
                "transparent_max_bounces",
            ):
                if hasattr(c, k):
                    self.cycles_attrs[k] = getattr(c, k)

        if hasattr(self.scene.render, "bake"):
            b = self.scene.render.bake
            for k in (
                "margin",
                "margin_type",
                "use_clear",
                "use_selected_to_active",
                "use_pass_direct",
                "use_pass_indirect",
                "use_pass_color",
                "use_pass_emit",
                "normal_space",
                "normal_r",
                "normal_g",
                "normal_b",
                "target",
            ):
                if hasattr(b, k):
                    self.bake_attrs[k] = getattr(b, k)

    def ensure_cycles(self, compute_device):
        try:
            self.scene.render.engine = "CYCLES"
        except Exception:
            bpy.ops.preferences.addon_enable(module="cycles")
            self.scene.render.engine = "CYCLES"

        if hasattr(self.scene, "cycles"):
            c = self.scene.cycles
            if compute_device in {"CPU", "GPU"} and hasattr(c, "device"):
                c.device = compute_device
            if hasattr(c, "use_denoising"):
                c.use_denoising = False
            if hasattr(c, "use_adaptive_sampling"):
                c.use_adaptive_sampling = False
            if hasattr(c, "max_bounces"):
                c.max_bounces = 0

        if hasattr(self.scene.render, "bake"):
            b = self.scene.render.bake
            if hasattr(b, "use_clear"):
                b.use_clear = True
            if hasattr(b, "use_selected_to_active"):
                b.use_selected_to_active = False
            if hasattr(b, "target"):
                b.target = "IMAGE_TEXTURES"
            if hasattr(b, "margin_type"):
                try:
                    b.margin_type = "ADJACENT_FACES"
                except Exception:
                    pass

    def restore(self):
        try:
            if bpy.context.object and bpy.context.object.mode != "OBJECT":
                bpy.ops.object.mode_set(mode="OBJECT")
        except Exception:
            pass

        try:
            if hasattr(self.scene, "cycles"):
                c = self.scene.cycles
                for k, v in self.cycles_attrs.items():
                    if hasattr(c, k):
                        try:
                            setattr(c, k, v)
                        except Exception:
                            pass
            if hasattr(self.scene.render, "bake"):
                b = self.scene.render.bake
                for k, v in self.bake_attrs.items():
                    if hasattr(b, k):
                        try:
                            setattr(b, k, v)
                        except Exception:
                            pass
            self.scene.render.engine = self.prev_engine
        except Exception:
            pass

        if not self.cycles_was_enabled and self.prev_engine != "CYCLES":
            try:
                if "cycles" in bpy.context.preferences.addons:
                    bpy.ops.preferences.addon_disable(module="cycles")
            except Exception:
                pass

        try:
            bpy.ops.object.select_all(action="DESELECT")
        except Exception:
            pass

        for obj in self.prev_selected:
            try:
                if obj and obj.name in bpy.data.objects:
                    obj.select_set(True)
            except Exception:
                pass

        try:
            if self.prev_active and self.prev_active.name in bpy.data.objects:
                self.context.view_layer.objects.active = self.prev_active
                if self.prev_mode and self.prev_mode != "OBJECT":
                    bpy.ops.object.mode_set(mode=self.prev_mode)
        except Exception:
            pass


def _find_material_output(nt):
    if nt is None:
        return None
    outputs = [n for n in nt.nodes if n.type == "OUTPUT_MATERIAL"]
    for out in outputs:
        if out.is_active_output and getattr(out, "target", "ALL") in {"ALL", "CYCLES"}:
            if out.inputs["Surface"].is_linked:
                return out
    for out in outputs:
        if out.inputs["Surface"].is_linked:
            return out
    for out in outputs:
        if out.is_active_output:
            return out
    return outputs[0] if outputs else None


def _get_emission_color_socket(node):
    return node.inputs.get("Emission Color") or node.inputs.get("Emission")


def _new_separate_rgb_node(nt):
    if hasattr(bpy.types, "ShaderNodeSeparateColor"):
        return nt.nodes.new("ShaderNodeSeparateColor")
    return nt.nodes.new("ShaderNodeSeparateRGB")


def _set_image_colorspace(img, candidates):
    for name in candidates:
        try:
            img.colorspace_settings.name = name
            return
        except Exception:
            continue


def _set_material_alpha_mode(mat, mode, threshold=0.5):
    if hasattr(mat, "blend_method"):
        try:
            mat.blend_method = mode
        except Exception:
            pass
    if hasattr(mat, "alpha_threshold"):
        try:
            mat.alpha_threshold = threshold
        except Exception:
            pass
    if hasattr(mat, "surface_render_method"):
        try:
            mat.surface_render_method = "BLENDED" if mode == "BLEND" else "DITHERED"
        except Exception:
            pass


def _make_default_pbr_material(name, tracker):
    mat = bpy.data.materials.new(name=name)
    mat.use_nodes = True
    tracker.materials.append(mat)
    return mat


def _unwrap_reroute_source(sock):
    visited = set()
    cur = sock
    while cur and cur.is_linked:
        link = cur.links[0]
        from_node = link.from_node
        if from_node is None:
            return None, None
        if from_node.type == "REROUTE" and from_node not in visited:
            visited.add(from_node)
            cur = from_node.inputs[0]
            continue
        return from_node, link.from_socket
    return None, None


def _is_pure_transparent_input(sock, depth=0):
    if depth > 16:
        return False
    from_node, _ = _unwrap_reroute_source(sock)
    if from_node is None:
        return False
    return from_node.type == "BSDF_TRANSPARENT"


def _bypass_transparent_mix(nt):
    """BaseColor/Metallic/Roughness/Normal用に MixShader(Shader, Transparent) をバイパスし、黒ずみを防ぐ。"""
    changed = True
    while changed:
        changed = False
        for node in list(nt.nodes):
            if node.type != "MIX_SHADER":
                continue
            s1 = node.inputs[1]
            s2 = node.inputs[2]
            t1 = _is_pure_transparent_input(s1)
            t2 = _is_pure_transparent_input(s2)
            keep_sock = None
            if t1 and not t2 and s2.is_linked:
                _, keep_sock = _unwrap_reroute_source(s2)
            elif t2 and not t1 and s1.is_linked:
                _, keep_sock = _unwrap_reroute_source(s1)
            if keep_sock is None:
                continue
            out_links = list(node.outputs[0].links)
            for l in out_links:
                nt.links.new(keep_sock, l.to_socket)
            nt.nodes.remove(node)
            changed = True
            break


def _collect_reachable_nodes(nt):
    out_node = _find_material_output(nt)
    if out_node is None:
        return [], out_node
    visited = set()
    stack = [out_node]
    while stack:
        n = stack.pop()
        if n in visited:
            continue
        visited.add(n)
        for inp in n.inputs:
            if inp.is_linked:
                for l in inp.links:
                    if l.from_node and l.from_node not in visited:
                        stack.append(l.from_node)
    return list(visited), out_node


def _inspect_material_requirements(mat, opt):
    """マテリアルが単一の定数Principledで済むか、ベイクが必要かを解析する。"""
    req = {
        "bake_base_color": False,
        "bake_metallic": False,
        "bake_roughness": False,
        "bake_normal": False,
        "bake_alpha": False,
        "bake_emission": False,
        "bake_transmission": False,
        "const_base": (0.8, 0.8, 0.8, 1.0),
        "const_metallic": 0.0,
        "const_roughness": 0.5,
        "const_alpha": 1.0,
        "const_transmission": 0.0,
        "const_ior": 1.45,
        "const_emission": (0.0, 0.0, 0.0),
        "const_emission_strength": 0.0,
    }

    if not mat.use_nodes or mat.node_tree is None:
        dc = tuple(getattr(mat, "diffuse_color", (0.8, 0.8, 0.8, 1.0)))
        req["const_base"] = (dc[0], dc[1], dc[2], dc[3] if len(dc) > 3 else 1.0)
        req["const_metallic"] = float(getattr(mat, "metallic", 0.0))
        req["const_roughness"] = float(getattr(mat, "roughness", 0.5))
        return req

    nodes, out_node = _collect_reachable_nodes(mat.node_tree)
    if out_node is None:
        return req

    surf_in = out_node.inputs.get("Surface")
    disp_in = out_node.inputs.get("Displacement")

    # 全ノードツリー（グループ含む）を走査して機能検出
    has_disp = bool(disp_in and disp_in.is_linked)
    shader_nodes = []
    has_mix_or_add = False
    has_transparent = False
    has_glass_node = False
    has_emission_node = False
    has_normal_link = has_disp
    has_direct_non_shader = False

    if surf_in and surf_in.is_linked and surf_in.links[0].from_socket.type != "SHADER":
        has_direct_non_shader = True

    def scan_tree(nt, visited_trees):
        nonlocal has_mix_or_add, has_transparent, has_glass_node, has_emission_node, has_normal_link
        if nt is None or nt in visited_trees:
            return
        visited_trees.add(nt)
        t_nodes, _ = _collect_reachable_nodes(nt) if nt == mat.node_tree else (list(nt.nodes), None)
        for n in t_nodes:
            if n.type in {"MIX_SHADER", "ADD_SHADER"}:
                has_mix_or_add = True
            elif n.type == "BSDF_TRANSPARENT":
                has_transparent = True
                shader_nodes.append(n)
            elif n.type in {"BSDF_GLASS", "BSDF_REFRACTION"}:
                has_glass_node = True
                shader_nodes.append(n)
            elif n.type == "EMISSION":
                has_emission_node = True
                shader_nodes.append(n)
            elif n.type in SHADER_TO_PBR_SPEC:
                shader_nodes.append(n)
            elif n.type == "GROUP" and n.node_tree is not None:
                scan_tree(n.node_tree, visited_trees)

            for norm_name in ("Normal", "Coat Normal", "Clearcoat Normal", "Tangent"):
                ns = n.inputs.get(norm_name)
                if ns and ns.is_linked:
                    has_normal_link = True

    scan_tree(mat.node_tree, set())

    # IOR の平均値・代表値を取得（単一Principled/Glassで忠実反映）
    for n in shader_nodes:
        ior_sock = n.inputs.get("IOR")
        if ior_sock and not ior_sock.is_linked:
            req["const_ior"] = max(1.0, min(3.0, float(ior_sock.default_value)))
            break

    # 最もシンプルなケース：リンク無しの単一Principled BSDF
    if (
        len(shader_nodes) == 1
        and shader_nodes[0].type == "BSDF_PRINCIPLED"
        and not has_mix_or_add
        and not has_direct_non_shader
    ):
        bsdf = shader_nodes[0]
        bc_sock = bsdf.inputs.get("Base Color")
        m_sock = bsdf.inputs.get("Metallic")
        r_sock = bsdf.inputs.get("Roughness")
        a_sock = bsdf.inputs.get("Alpha")
        ec_sock = _get_emission_color_socket(bsdf)
        es_sock = bsdf.inputs.get("Emission Strength")
        trans_sock = bsdf.inputs.get("Transmission Weight") or bsdf.inputs.get("Transmission")

        if bc_sock:
            if bc_sock.is_linked:
                req["bake_base_color"] = opt["bake_base_color"]
            else:
                req["const_base"] = tuple(bc_sock.default_value)[:4]

        if m_sock:
            if m_sock.is_linked:
                req["bake_metallic"] = opt["bake_orm"]
            else:
                req["const_metallic"] = float(m_sock.default_value)

        if r_sock:
            if r_sock.is_linked:
                req["bake_roughness"] = opt["bake_orm"]
            else:
                req["const_roughness"] = float(r_sock.default_value)

        if has_normal_link and opt["bake_normal"]:
            req["bake_normal"] = True

        if a_sock:
            if a_sock.is_linked:
                req["bake_alpha"] = opt["bake_alpha"]
            else:
                req["const_alpha"] = float(a_sock.default_value)

        if trans_sock:
            if trans_sock.is_linked:
                req["bake_transmission"] = opt["bake_transmission"]
            else:
                req["const_transmission"] = float(trans_sock.default_value)

        if ec_sock and opt["bake_emission"]:
            es_linked = bool(es_sock and es_sock.is_linked)
            es_val = float(es_sock.default_value) if es_sock else 1.0
            ec_linked = ec_sock.is_linked
            ec_val = tuple(ec_sock.default_value)[:3]
            ec_nonblack = max(ec_val) > 1e-4
            if (ec_linked or ec_nonblack) and (es_linked or es_val > 1e-4):
                if ec_linked or es_linked:
                    req["bake_emission"] = True
                else:
                    req["const_emission"] = ec_val
                    req["const_emission_strength"] = es_val

        return req

    # リンク構成を含まない単一 Glass / Refraction BSDF の高速パス（不要なテクスチャ焼き付けを回避）
    if (
        len(shader_nodes) == 1
        and shader_nodes[0].type in {"BSDF_GLASS", "BSDF_REFRACTION"}
        and not has_mix_or_add
        and not has_direct_non_shader
    ):
        gs = shader_nodes[0]
        c_sock = gs.inputs.get("Color")
        r_sock = gs.inputs.get("Roughness")

        req["const_metallic"] = 0.0
        req["const_alpha"] = 1.0
        req["const_transmission"] = 1.0 if opt["bake_transmission"] else 0.0

        if c_sock:
            if c_sock.is_linked:
                req["bake_base_color"] = opt["bake_base_color"]
            else:
                req["const_base"] = tuple(c_sock.default_value)[:4]

        if r_sock:
            if r_sock.is_linked:
                req["bake_roughness"] = opt["bake_orm"]
            else:
                req["const_roughness"] = float(r_sock.default_value)

        if has_normal_link and opt["bake_normal"]:
            req["bake_normal"] = True

        return req

    # 複雑なノード構成：必要なチャンネルをベイク対象にする
    req["bake_base_color"] = opt["bake_base_color"]
    req["bake_metallic"] = opt["bake_orm"]
    req["bake_roughness"] = opt["bake_orm"]
    req["bake_normal"] = opt["bake_normal"] and has_normal_link
    req["bake_alpha"] = opt["bake_alpha"] and (
        has_transparent
        or any(
            (n.inputs.get("Alpha") and (n.inputs["Alpha"].is_linked or float(n.inputs["Alpha"].default_value) < 0.999))
            for n in shader_nodes
            if n.type == "BSDF_PRINCIPLED"
        )
    )
    req["bake_transmission"] = opt["bake_transmission"] and (
        has_glass_node
        or any(
            ((ts := (n.inputs.get("Transmission Weight") or n.inputs.get("Transmission"))) is not None and (ts.is_linked or float(ts.default_value) > 1e-4))
            for n in shader_nodes
            if n.type == "BSDF_PRINCIPLED"
        )
    )

    any_em = has_emission_node or has_direct_non_shader
    if not any_em:
        for n in shader_nodes:
            if n.type == "BSDF_PRINCIPLED":
                ec = _get_emission_color_socket(n)
                es = n.inputs.get("Emission Strength")
                es_on = bool(es and (es.is_linked or float(es.default_value) > 1e-4))
                ec_on = bool(ec and (ec.is_linked or max(tuple(ec.default_value)[:3]) > 1e-4))
                if es_on and ec_on:
                    any_em = True
                    break
    req["bake_emission"] = opt["bake_emission"] and any_em
    return req


def _copy_socket_to_emission_color(nt, src_sock, dst_color_sock, fallback_rgba=(0.0, 0.0, 0.0, 1.0)):
    if src_sock is None:
        dst_color_sock.default_value = fallback_rgba
        return
    if src_sock.is_linked:
        nt.links.new(src_sock.links[0].from_socket, dst_color_sock)
        return
    val = getattr(src_sock, "default_value", None)
    if val is None:
        dst_color_sock.default_value = fallback_rgba
    elif isinstance(val, (int, float)):
        f = float(val)
        dst_color_sock.default_value = (f, f, f, 1.0)
    else:
        t = tuple(val)
        if len(t) == 3:
            dst_color_sock.default_value = (t[0], t[1], t[2], 1.0)
        elif len(t) >= 4:
            dst_color_sock.default_value = (t[0], t[1], t[2], t[3])
        else:
            dst_color_sock.default_value = fallback_rgba


def _copy_socket_to_float(nt, src_sock, dst_float_sock, fallback_val=1.0):
    if src_sock is None:
        dst_float_sock.default_value = fallback_val
        return
    if src_sock.is_linked:
        nt.links.new(src_sock.links[0].from_socket, dst_float_sock)
        return
    val = getattr(src_sock, "default_value", fallback_val)
    dst_float_sock.default_value = float(val)


def _rewrite_tree_for_channel(nt, channel, tracker, group_cache, is_root=True):
    """ノードツリー内のBSDFを指定チャンネル用のEmission出力へ書き換える。"""
    if nt is None:
        return

    if channel in {"BASE_COLOR", "METALLIC", "ROUGHNESS", "NORMAL", "TRANSMISSION"}:
        _bypass_transparent_mix(nt)

    if channel == "NORMAL":
        # Normal pass は内部の Group も複製して Transparentバイパスを適用可能にする
        for node in list(nt.nodes):
            if node.type == "GROUP" and node.node_tree is not None:
                orig_gt = node.node_tree
                if orig_gt not in group_cache:
                    cloned_gt = orig_gt.copy()
                    tracker.node_groups.append(cloned_gt)
                    group_cache[orig_gt] = cloned_gt
                    _rewrite_tree_for_channel(cloned_gt, channel, tracker, group_cache, is_root=False)
                node.node_tree = group_cache[orig_gt]
        return

    # Rootの出力にシェーダー以外（Color/Value/Vector）が直結されている場合に対応
    if is_root:
        out_node = _find_material_output(nt)
        if out_node:
            surf = out_node.inputs.get("Surface")
            if surf and surf.is_linked and surf.links[0].from_socket.type != "SHADER":
                orig_from = surf.links[0].from_socket
                em = nt.nodes.new("ShaderNodeEmission")
                if channel in {"BASE_COLOR", "EMISSION"}:
                    nt.links.new(orig_from, em.inputs["Color"])
                elif channel == "METALLIC":
                    em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
                elif channel == "ROUGHNESS":
                    em.inputs["Color"].default_value = (0.5, 0.5, 0.5, 1.0)
                elif channel == "ALPHA":
                    em.inputs["Color"].default_value = (1.0, 1.0, 1.0, 1.0)
                elif channel == "TRANSMISSION":
                    em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
                em.inputs["Strength"].default_value = 1.0
                nt.links.new(em.outputs["Emission"], surf)

    # 先にグループノードを再帰処理
    for node in list(nt.nodes):
        if node.type == "GROUP" and node.node_tree is not None:
            orig_gt = node.node_tree
            if orig_gt not in group_cache:
                cloned_gt = orig_gt.copy()
                tracker.node_groups.append(cloned_gt)
                group_cache[orig_gt] = cloned_gt
                _rewrite_tree_for_channel(cloned_gt, channel, tracker, group_cache, is_root=False)
            node.node_tree = group_cache[orig_gt]

    for node in list(nt.nodes):
        spec = SHADER_TO_PBR_SPEC.get(node.type)
        if not spec:
            continue

        out_links = []
        for out_sock in node.outputs:
            if out_sock.type == "SHADER":
                for l in list(out_sock.links):
                    out_links.append(l.to_socket)

        if not out_links:
            continue

        em = nt.nodes.new("ShaderNodeEmission")
        em.inputs["Strength"].default_value = 1.0

        if spec == "PRINCIPLED":
            if channel == "BASE_COLOR":
                _copy_socket_to_emission_color(nt, node.inputs.get("Base Color"), em.inputs["Color"], (0.8, 0.8, 0.8, 1.0))
            elif channel == "METALLIC":
                _copy_socket_to_emission_color(nt, node.inputs.get("Metallic"), em.inputs["Color"], (0.0, 0.0, 0.0, 1.0))
            elif channel == "ROUGHNESS":
                _copy_socket_to_emission_color(nt, node.inputs.get("Roughness"), em.inputs["Color"], (0.5, 0.5, 0.5, 1.0))
            elif channel == "ALPHA":
                _copy_socket_to_emission_color(nt, node.inputs.get("Alpha"), em.inputs["Color"], (1.0, 1.0, 1.0, 1.0))
            elif channel == "TRANSMISSION":
                trans_sock = node.inputs.get("Transmission Weight") or node.inputs.get("Transmission")
                _copy_socket_to_emission_color(nt, trans_sock, em.inputs["Color"], (0.0, 0.0, 0.0, 1.0))
            elif channel == "EMISSION":
                ec_sock = _get_emission_color_socket(node)
                es_sock = node.inputs.get("Emission Strength")
                _copy_socket_to_emission_color(nt, ec_sock, em.inputs["Color"], (0.0, 0.0, 0.0, 1.0))
                _copy_socket_to_float(nt, es_sock, em.inputs["Strength"], 1.0)

        elif spec == "DIFFUSE_LIKE":
            if channel == "BASE_COLOR":
                _copy_socket_to_emission_color(nt, node.inputs.get("Color"), em.inputs["Color"], (0.8, 0.8, 0.8, 1.0))
            elif channel == "METALLIC":
                em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
            elif channel == "ROUGHNESS":
                _copy_socket_to_emission_color(nt, node.inputs.get("Roughness"), em.inputs["Color"], (0.7, 0.7, 0.7, 1.0))
            elif channel == "ALPHA":
                em.inputs["Color"].default_value = (1.0, 1.0, 1.0, 1.0)
            elif channel == "TRANSMISSION":
                em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
            elif channel == "EMISSION":
                em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
                em.inputs["Strength"].default_value = 0.0

        elif spec == "GLOSSY_LIKE":
            if channel == "BASE_COLOR":
                _copy_socket_to_emission_color(nt, node.inputs.get("Color"), em.inputs["Color"], (0.8, 0.8, 0.8, 1.0))
            elif channel == "METALLIC":
                em.inputs["Color"].default_value = (1.0, 1.0, 1.0, 1.0)
            elif channel == "ROUGHNESS":
                _copy_socket_to_emission_color(nt, node.inputs.get("Roughness"), em.inputs["Color"], (0.2, 0.2, 0.2, 1.0))
            elif channel == "ALPHA":
                em.inputs["Color"].default_value = (1.0, 1.0, 1.0, 1.0)
            elif channel == "TRANSMISSION":
                em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
            elif channel == "EMISSION":
                em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
                em.inputs["Strength"].default_value = 0.0

        elif spec == "GLASS_LIKE":
            if channel == "BASE_COLOR":
                _copy_socket_to_emission_color(nt, node.inputs.get("Color"), em.inputs["Color"], (1.0, 1.0, 1.0, 1.0))
            elif channel == "METALLIC":
                em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
            elif channel == "ROUGHNESS":
                _copy_socket_to_emission_color(nt, node.inputs.get("Roughness"), em.inputs["Color"], (0.05, 0.05, 0.05, 1.0))
            elif channel == "ALPHA":
                em.inputs["Color"].default_value = (1.0, 1.0, 1.0, 1.0)
            elif channel == "TRANSMISSION":
                em.inputs["Color"].default_value = (1.0, 1.0, 1.0, 1.0)
            elif channel == "EMISSION":
                em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
                em.inputs["Strength"].default_value = 0.0

        elif spec == "TRANSPARENT":
            if channel == "BASE_COLOR":
                _copy_socket_to_emission_color(nt, node.inputs.get("Color"), em.inputs["Color"], (1.0, 1.0, 1.0, 1.0))
            elif channel == "METALLIC":
                em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
            elif channel == "ROUGHNESS":
                em.inputs["Color"].default_value = (0.5, 0.5, 0.5, 1.0)
            elif channel == "ALPHA":
                em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
            elif channel == "TRANSMISSION":
                em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
            elif channel == "EMISSION":
                em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
                em.inputs["Strength"].default_value = 0.0

        elif spec == "EMISSION":
            if channel == "BASE_COLOR":
                em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
            elif channel == "METALLIC":
                em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
            elif channel == "ROUGHNESS":
                em.inputs["Color"].default_value = (0.5, 0.5, 0.5, 1.0)
            elif channel == "ALPHA":
                em.inputs["Color"].default_value = (1.0, 1.0, 1.0, 1.0)
            elif channel == "TRANSMISSION":
                em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
            elif channel == "EMISSION":
                _copy_socket_to_emission_color(nt, node.inputs.get("Color"), em.inputs["Color"], (1.0, 1.0, 1.0, 1.0))
                _copy_socket_to_float(nt, node.inputs.get("Strength"), em.inputs["Strength"], 1.0)

        for dst_sock in out_links:
            nt.links.new(em.outputs["Emission"], dst_sock)

        nt.nodes.remove(node)


def _attach_bake_target_image(mat, img):
    if not mat.use_nodes:
        mat.use_nodes = True
    nt = mat.node_tree
    for n in nt.nodes:
        n.select = False
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.name = "__YMM4_BakeTargetTex"
    tex.image = img
    tex.select = True
    nt.nodes.active = tex
    return tex


def _build_channel_material(src_mat, channel, target_img, tracker):
    work_mat = src_mat.copy()
    tracker.materials.append(work_mat)
    if not work_mat.use_nodes:
        work_mat.use_nodes = True
    if work_mat.node_tree is not None:
        _rewrite_tree_for_channel(work_mat.node_tree, channel, tracker, {}, is_root=True)
    _attach_bake_target_image(work_mat, target_img)
    return work_mat


def _create_dummy_bake_material(dummy_img, tracker):
    mat = bpy.data.materials.new(name="__YMM4_DummyBakeMat")
    mat.use_nodes = True
    tracker.materials.append(mat)
    nt = mat.node_tree
    for n in list(nt.nodes):
        if n.type != "OUTPUT_MATERIAL":
            nt.nodes.remove(n)
    out = _find_material_output(nt) or nt.nodes.new("ShaderNodeOutputMaterial")
    em = nt.nodes.new("ShaderNodeEmission")
    em.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
    em.inputs["Strength"].default_value = 0.0
    nt.links.new(em.outputs["Emission"], out.inputs["Surface"])
    _attach_bake_target_image(mat, dummy_img)
    return mat


def _duplicate_target_objects(context, source_objects, apply_modifiers, tracker):
    temp_col = bpy.data.collections.new("_YMM4_D3D11_TempBake")
    context.scene.collection.children.link(temp_col)
    tracker.collections.append(temp_col)

    depsgraph = context.evaluated_depsgraph_get()
    dups = []

    for src in source_objects:
        if src.type not in SUPPORTED_OBJECT_TYPES:
            continue

        # 元オブジェクトのスロットから実マテリアルリストを取得（OBJECT/DATA両対応）
        slot_mats = [s.material for s in src.material_slots]

        if apply_modifiers or src.type != "MESH":
            eval_obj = src.evaluated_get(depsgraph)
            new_mesh = bpy.data.meshes.new_from_object(
                eval_obj, preserve_all_data_layers=True, depsgraph=depsgraph
            )
        else:
            new_mesh = src.data.copy()

        if new_mesh is None or len(new_mesh.polygons) == 0:
            if new_mesh is not None and new_mesh.name in bpy.data.meshes:
                bpy.data.meshes.remove(new_mesh, do_unlink=True)
            continue

        tracker.meshes.append(new_mesh)

        dup_obj = bpy.data.objects.new(name=src.name, object_data=new_mesh)
        dup_obj.matrix_world = src.matrix_world.copy()
        temp_col.objects.link(dup_obj)
        tracker.objects.append(dup_obj)

        # 全スロットを安全に独立コピーへ差し替え
        if len(dup_obj.data.materials) == 0:
            if slot_mats:
                for m in slot_mats:
                    copied = m.copy() if m else _make_default_pbr_material("__YMM4_Default", tracker)
                    if m:
                        tracker.materials.append(copied)
                    dup_obj.data.materials.append(copied)
            else:
                dup_obj.data.materials.append(_make_default_pbr_material("__YMM4_Default", tracker))
        else:
            for idx in range(len(dup_obj.data.materials)):
                orig_m = slot_mats[idx] if idx < len(slot_mats) and slot_mats[idx] is not None else dup_obj.data.materials[idx]
                if orig_m is None:
                    copied = _make_default_pbr_material(f"__YMM4_Default_{idx}", tracker)
                else:
                    copied = orig_m.copy()
                    tracker.materials.append(copied)
                dup_obj.data.materials[idx] = copied

        used_indices = sorted({p.material_index for p in new_mesh.polygons})
        dups.append((dup_obj, used_indices))

    return dups


def _prepare_bake_uvs(context, dup_obj, used_mat_indices, uv_mode, angle_deg, island_margin):
    mesh = dup_obj.data
    has_uv = len(mesh.uv_layers) > 0
    need_new_uv = (uv_mode == "ALWAYS_NEW") or (not has_uv)

    if not need_new_uv:
        return None

    orig_render_uv = None
    for uv in mesh.uv_layers:
        if uv.active_render:
            orig_render_uv = uv
            break
    if orig_render_uv is None and has_uv:
        orig_render_uv = mesh.uv_layers[0]

    bake_uv = mesh.uv_layers.new(name="__YMM4_BakeUV")
    bake_uv_name = bake_uv.name
    mesh.uv_layers.active = bake_uv
    if orig_render_uv is not None:
        orig_render_uv.active_render = True
    else:
        bake_uv.active_render = True

    bpy.ops.object.select_all(action="DESELECT")
    dup_obj.select_set(True)
    context.view_layer.objects.active = dup_obj

    smart_props = bpy.ops.uv.smart_project.get_rna_type().properties
    raw_kwargs = {
        "angle_limit": math.radians(max(1.0, min(89.0, angle_deg))),
        "island_margin": island_margin,
        "correct_aspect": True,
        "scale_to_bounds": True,
    }
    smart_kwargs = {k: v for k, v in raw_kwargs.items() if k in smart_props}

    # マテリアルスロットごとに個別の[0..1]UV空間へ展開して解像度を最大限活用
    target_groups = used_mat_indices if used_mat_indices else [0]
    for mat_idx in target_groups:
        bpy.ops.object.mode_set(mode="EDIT")
        bm = bmesh.from_edit_mesh(mesh)
        bm.faces.ensure_lookup_table()
        count = 0
        for f in bm.faces:
            sel = (f.material_index == mat_idx) or (len(dup_obj.data.materials) == 0)
            f.select_set(sel)
            if sel:
                count += 1
        bmesh.update_edit_mesh(mesh)
        if count > 0:
            bpy.ops.uv.smart_project(**smart_kwargs)
        bpy.ops.object.mode_set(mode="OBJECT")

    return bake_uv_name


def _finalize_uvs(dup_obj, bake_uv_name):
    mesh = dup_obj.data
    if bake_uv_name and bake_uv_name in mesh.uv_layers:
        bake_uv = mesh.uv_layers[bake_uv_name]
        if len(mesh.uv_layers) > 1:
            n_loops = len(bake_uv.data)
            if n_loops > 0:
                flat = np.empty(n_loops * 2, dtype=np.float32)
                bake_uv.data.foreach_get("uv", flat)
                mesh.uv_layers[0].data.foreach_set("uv", flat)
            while len(mesh.uv_layers) > 1:
                mesh.uv_layers.remove(mesh.uv_layers[-1])
        uv = mesh.uv_layers[0]
        uv.name = "UVMap"
        mesh.uv_layers.active = uv
        uv.active_render = True
    elif len(mesh.uv_layers) > 0:
        uv = mesh.uv_layers.active or mesh.uv_layers[0]
        uv.active_render = True


def _read_pixels_np(img):
    w, h = img.size[0], img.size[1]
    arr = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(arr)
    return arr.reshape((h, w, 4))


def _write_pixels_np(img, arr_hw4):
    flat = np.ascontiguousarray(arr_hw4, dtype=np.float32).ravel()
    img.pixels.foreach_set(flat)
    img.update()


def _is_flat_channel(arr_2d, eps=0.008):
    vmin = float(np.min(arr_2d))
    vmax = float(np.max(arr_2d))
    if (vmax - vmin) <= eps:
        return True, float(np.mean(arr_2d))
    return False, 0.0


def _linear_to_srgb_np(x):
    x = np.clip(x, 0.0, 1.0)
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(x, 1.0 / 2.4) - 0.055)


def _assemble_gltf_material(slot_name, req, baked_imgs, tex_size, tracker):
    """ベイク済み画像から、Blender glTFエクスポーターが正しくGLB化できるクリーンなPrincipledマテリアルを構築する。"""
    mat = bpy.data.materials.new(name=slot_name)
    mat.use_nodes = True
    tracker.materials.append(mat)
    nt = mat.node_tree

    for n in list(nt.nodes):
        if n.type not in {"OUTPUT_MATERIAL", "BSDF_PRINCIPLED"}:
            nt.nodes.remove(n)

    out = _find_material_output(nt) or nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if bsdf is None:
        bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])

    # 1. Base Color & Alpha
    bc_img = baked_imgs.get("BASE_COLOR")
    a_img = baked_imgs.get("ALPHA")

    alpha_pixels = None
    alpha_mode = "OPAQUE"
    if a_img is not None:
        a_px = np.clip(_read_pixels_np(a_img)[:, :, 0], 0.0, 1.0)
        if float(np.min(a_px)) < 0.994:
            alpha_pixels = a_px
            if np.any((a_px > 0.05) & (a_px < 0.95)):
                alpha_mode = "BLEND"
            else:
                alpha_mode = "CLIP"
    elif req["const_alpha"] < 0.994:
        alpha_mode = "BLEND"

    if bc_img is not None:
        bc_px = np.clip(_read_pixels_np(bc_img), 0.0, 1.0)
        rgb_flat = (
            _is_flat_channel(bc_px[:, :, 0])[0]
            and _is_flat_channel(bc_px[:, :, 1])[0]
            and _is_flat_channel(bc_px[:, :, 2])[0]
        )
        if rgb_flat and alpha_pixels is None:
            # 均一のsRGBベイク値 -> Linearに戻さずともsRGB画像のまま使うより定数化した方が超軽量
            r_srgb = float(np.mean(bc_px[:, :, 0]))
            g_srgb = float(np.mean(bc_px[:, :, 1]))
            b_srgb = float(np.mean(bc_px[:, :, 2]))
            def srgb2lin(v):
                return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4
            bsdf.inputs["Base Color"].default_value = (srgb2lin(r_srgb), srgb2lin(g_srgb), srgb2lin(b_srgb), 1.0)
            if alpha_mode == "BLEND":
                bsdf.inputs["Alpha"].default_value = req["const_alpha"]
                _set_material_alpha_mode(mat, "BLEND")
        else:
            if alpha_pixels is not None:
                rgba_img = bpy.data.images.new(
                    name=f"{slot_name}_BaseColorA",
                    width=bc_img.size[0],
                    height=bc_img.size[1],
                    alpha=True,
                    float_buffer=False,
                )
                tracker.images.append(rgba_img)
                _set_image_colorspace(rgba_img, ("sRGB",))
                rgba_img.alpha_mode = "STRAIGHT"
                bc_px[:, :, 3] = alpha_pixels
                _write_pixels_np(rgba_img, bc_px)
                rgba_img.pack()
                tex = nt.nodes.new("ShaderNodeTexImage")
                tex.image = rgba_img
                nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
                nt.links.new(tex.outputs["Alpha"], bsdf.inputs["Alpha"])
                _set_material_alpha_mode(mat, alpha_mode, 0.5)
            else:
                bc_img.alpha_mode = "STRAIGHT"
                bc_img.update()
                bc_img.pack()
                tex = nt.nodes.new("ShaderNodeTexImage")
                tex.image = bc_img
                nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
                if alpha_mode == "BLEND":
                    bsdf.inputs["Alpha"].default_value = req["const_alpha"]
                    _set_material_alpha_mode(mat, "BLEND")
    else:
        if alpha_pixels is not None:
            # BaseColor定数 × Alphaマップ -> RGBA画像化
            def lin2srgb(v):
                return v * 12.92 if v <= 0.0031308 else 1.055 * (v ** (1.0 / 2.4)) - 0.055
            cb = req["const_base"]
            h, w = alpha_pixels.shape
            rgba_img = bpy.data.images.new(
                name=f"{slot_name}_BaseColorA",
                width=w,
                height=h,
                alpha=True,
                float_buffer=False,
            )
            tracker.images.append(rgba_img)
            _set_image_colorspace(rgba_img, ("sRGB",))
            rgba_img.alpha_mode = "STRAIGHT"
            arr = np.empty((h, w, 4), dtype=np.float32)
            arr[:, :, 0] = lin2srgb(max(0.0, cb[0]))
            arr[:, :, 1] = lin2srgb(max(0.0, cb[1]))
            arr[:, :, 2] = lin2srgb(max(0.0, cb[2]))
            arr[:, :, 3] = alpha_pixels
            _write_pixels_np(rgba_img, arr)
            rgba_img.pack()
            tex = nt.nodes.new("ShaderNodeTexImage")
            tex.image = rgba_img
            nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
            nt.links.new(tex.outputs["Alpha"], bsdf.inputs["Alpha"])
            _set_material_alpha_mode(mat, alpha_mode, 0.5)
        else:
            bsdf.inputs["Base Color"].default_value = req["const_base"]
            if alpha_mode == "BLEND":
                bsdf.inputs["Alpha"].default_value = req["const_alpha"]
                _set_material_alpha_mode(mat, "BLEND")

    # 2. Roughness (G) + Metallic (B) ORMパック
    r_img = baked_imgs.get("ROUGHNESS")
    m_img = baked_imgs.get("METALLIC")

    r_arr = None
    r_const = req["const_roughness"]
    if r_img is not None:
        r_px = np.clip(_read_pixels_np(r_img)[:, :, 0], 0.0, 1.0)
        flat, mean_val = _is_flat_channel(r_px)
        if flat:
            r_const = mean_val
        else:
            r_arr = r_px

    m_arr = None
    m_const = req["const_metallic"]
    if m_img is not None:
        m_px = np.clip(_read_pixels_np(m_img)[:, :, 0], 0.0, 1.0)
        flat, mean_val = _is_flat_channel(m_px)
        if flat:
            m_const = mean_val
        else:
            m_arr = m_px

    if r_arr is not None or m_arr is not None:
        ref = r_arr if r_arr is not None else m_arr
        h, w = ref.shape
        orm_arr = np.ones((h, w, 4), dtype=np.float32)
        orm_arr[:, :, 1] = r_arr if r_arr is not None else r_const
        orm_arr[:, :, 2] = m_arr if m_arr is not None else m_const
        orm_img = bpy.data.images.new(
            name=f"{slot_name}_ORM",
            width=w,
            height=h,
            alpha=False,
            float_buffer=False,
        )
        tracker.images.append(orm_img)
        _set_image_colorspace(orm_img, ("Non-Color", "Raw", "Linear"))
        _write_pixels_np(orm_img, orm_arr)
        orm_img.pack()

        tex = nt.nodes.new("ShaderNodeTexImage")
        tex.image = orm_img
        sep = _new_separate_rgb_node(nt)
        nt.links.new(tex.outputs["Color"], sep.inputs[0])
        nt.links.new(sep.outputs[1], bsdf.inputs["Roughness"])
        nt.links.new(sep.outputs[2], bsdf.inputs["Metallic"])
    else:
        bsdf.inputs["Roughness"].default_value = r_const
        bsdf.inputs["Metallic"].default_value = m_const

    # 3. Normal Map
    n_img = baked_imgs.get("NORMAL")
    if n_img is not None:
        n_px = np.clip(_read_pixels_np(n_img)[:, :, :3], 0.0, 1.0)
        dev = float(np.max(np.abs(n_px - np.array([0.5, 0.5, 1.0], dtype=np.float32))))
        if dev > 0.015:
            n_img.update()
            n_img.pack()
            tex = nt.nodes.new("ShaderNodeTexImage")
            tex.image = n_img
            nmap = nt.nodes.new("ShaderNodeNormalMap")
            nmap.space = "TANGENT"
            nt.links.new(tex.outputs["Color"], nmap.inputs["Color"])
            nt.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])

    # 4. Emission (HDR強度維持)
    em_sock = _get_emission_color_socket(bsdf)
    em_str_sock = bsdf.inputs.get("Emission Strength")
    em_img = baked_imgs.get("EMISSION")

    if em_img is not None and em_sock is not None:
        em_rgb = np.maximum(_read_pixels_np(em_img)[:, :, :3], 0.0)
        peak = float(np.max(em_rgb))
        if peak > 1e-4:
            strength = peak if peak > 1.0 else 1.0
            norm_lin = np.clip(em_rgb / strength, 0.0, 1.0)
            flat = (
                _is_flat_channel(norm_lin[:, :, 0])[0]
                and _is_flat_channel(norm_lin[:, :, 1])[0]
                and _is_flat_channel(norm_lin[:, :, 2])[0]
            )
            if flat:
                r_m = float(np.mean(norm_lin[:, :, 0]))
                g_m = float(np.mean(norm_lin[:, :, 1]))
                b_m = float(np.mean(norm_lin[:, :, 2]))
                em_sock.default_value = (r_m, g_m, b_m, 1.0)
                if em_str_sock is not None:
                    em_str_sock.default_value = strength
            else:
                h, w, _ = norm_lin.shape
                em_srgb_img = bpy.data.images.new(
                    name=f"{slot_name}_Emissive",
                    width=w,
                    height=h,
                    alpha=False,
                    float_buffer=False,
                )
                tracker.images.append(em_srgb_img)
                _set_image_colorspace(em_srgb_img, ("sRGB",))
                out_arr = np.ones((h, w, 4), dtype=np.float32)
                out_arr[:, :, :3] = _linear_to_srgb_np(norm_lin)
                _write_pixels_np(em_srgb_img, out_arr)
                em_srgb_img.pack()

                tex = nt.nodes.new("ShaderNodeTexImage")
                tex.image = em_srgb_img
                nt.links.new(tex.outputs["Color"], em_sock)
                if em_str_sock is not None:
                    em_str_sock.default_value = strength
    elif req["const_emission_strength"] > 1e-4 and em_sock is not None:
        ce = req["const_emission"]
        em_sock.default_value = (ce[0], ce[1], ce[2], 1.0)
        if em_str_sock is not None:
            em_str_sock.default_value = req["const_emission_strength"]

    # 5. Transmission & IOR (KHR_materials_transmission / KHR_materials_ior)
    ior_sock = bsdf.inputs.get("IOR")
    if ior_sock is not None:
        ior_sock.default_value = req["const_ior"]

    tr_sock = bsdf.inputs.get("Transmission Weight") or bsdf.inputs.get("Transmission")
    tr_img = baked_imgs.get("TRANSMISSION")
    if tr_sock is not None:
        if tr_img is not None:
            tr_px = np.clip(_read_pixels_np(tr_img)[:, :, 0], 0.0, 1.0)
            flat, mean_val = _is_flat_channel(tr_px)
            if flat:
                tr_sock.default_value = mean_val
            else:
                tr_img.update()
                tr_img.pack()
                tex = nt.nodes.new("ShaderNodeTexImage")
                tex.image = tr_img
                nt.links.new(tex.outputs["Color"], tr_sock)
        else:
            tr_sock.default_value = req["const_transmission"]

    return mat


def _bake_single_object(context, dup_obj, used_mat_indices, opt, tracker):
    tex_size = int(opt["texture_size"])
    bake_uv_name = _prepare_bake_uvs(
        context,
        dup_obj,
        used_mat_indices,
        opt["uv_mode"],
        opt["uv_angle_limit"],
        opt["uv_island_margin"],
    )

    slot_count = len(dup_obj.data.materials)
    orig_copied_mats = [dup_obj.data.materials[i] for i in range(slot_count)]
    reqs = {}
    for i in used_mat_indices:
        if 0 <= i < slot_count and orig_copied_mats[i] is not None:
            reqs[i] = _inspect_material_requirements(orig_copied_mats[i], opt)

    dummy_img = bpy.data.images.new(
        name="__YMM4_DummyBake", width=4, height=4, alpha=False, float_buffer=False
    )
    tracker.images.append(dummy_img)
    _set_image_colorspace(dummy_img, ("Non-Color", "Raw", "Linear"))
    dummy_mat = _create_dummy_bake_material(dummy_img, tracker)

    # slot_idx -> {channel: bpy.types.Image}
    baked_map = {i: {} for i in reqs.keys()}

    bpy.ops.object.select_all(action="DESELECT")
    dup_obj.select_set(True)
    context.view_layer.objects.active = dup_obj

    scene = context.scene
    scene.render.bake.margin = opt["bake_margin"]

    channels = [
        ("BASE_COLOR", "bake_base_color", "EMIT", ("sRGB",), False),
        ("METALLIC", "bake_metallic", "EMIT", ("Non-Color", "Raw", "Linear"), True),
        ("ROUGHNESS", "bake_roughness", "EMIT", ("Non-Color", "Raw", "Linear"), True),
        ("TRANSMISSION", "bake_transmission", "EMIT", ("Non-Color", "Raw", "Linear"), True),
        ("ALPHA", "bake_alpha", "EMIT", ("Non-Color", "Raw", "Linear"), True),
        ("EMISSION", "bake_emission", "EMIT", ("Non-Color", "Raw", "Linear"), True),
        ("NORMAL", "bake_normal", "NORMAL", ("Non-Color", "Raw", "Linear"), False),
    ]

    for ch_name, req_flag, bake_type, cs_candidates, use_float in channels:
        active_slots = [i for i, r in reqs.items() if r[req_flag]]
        if not active_slots:
            continue

        # 他のチャンネル実行時に他スロットのノードを壊さないよう、全スロットに今回用の作業マテリアルを割り当て
        for slot_idx in range(slot_count):
            if slot_idx in active_slots:
                img = bpy.data.images.new(
                    name=f"__YMM4_{dup_obj.name}_{slot_idx}_{ch_name}",
                    width=tex_size,
                    height=tex_size,
                    alpha=False,
                    float_buffer=use_float,
                )
                tracker.images.append(img)
                _set_image_colorspace(img, cs_candidates)
                baked_map[slot_idx][ch_name] = img
                work_mat = _build_channel_material(orig_copied_mats[slot_idx], ch_name, img, tracker)
                dup_obj.data.materials[slot_idx] = work_mat
            else:
                dup_obj.data.materials[slot_idx] = dummy_mat

        if bake_type == "NORMAL":
            scene.cycles.samples = opt["bake_samples"]
            scene.render.bake.normal_space = "TANGENT"
            scene.render.bake.normal_r = "POS_X"
            scene.render.bake.normal_g = "POS_Y"
            scene.render.bake.normal_b = "POS_Z"
        else:
            scene.cycles.samples = min(opt["bake_samples"], 4)

        bpy.ops.object.bake(type=bake_type)

    # 最終的なGLBエクスポート用マテリアルを組み立てて各スロットへ割り当て
    for slot_idx in range(slot_count):
        if slot_idx in reqs:
            src_name = orig_copied_mats[slot_idx].name.split(".")[0] or f"Mat_{slot_idx}"
            final_mat = _assemble_gltf_material(
                f"{src_name}_YMM4",
                reqs[slot_idx],
                baked_map[slot_idx],
                tex_size,
                tracker,
            )
            dup_obj.data.materials[slot_idx] = final_mat
        else:
            dup_obj.data.materials[slot_idx] = dummy_mat

    _finalize_uvs(dup_obj, bake_uv_name)


def run_bake_and_export(context, filepath, opt):
    scene = context.scene
    if opt["target_mode"] == "SELECTED":
        sources = [o for o in context.selected_objects if o.type in SUPPORTED_OBJECT_TYPES]
    else:
        sources = [
            o
            for o in context.view_layer.objects
            if o.visible_get() and o.type in SUPPORTED_OBJECT_TYPES
        ]

    if not sources:
        return False, "書き出し対象のメッシュオブジェクトが見つかりません。"

    snap = SceneSnapshot(context)
    tracker = TempResourceTracker()

    try:
        if context.object and context.object.mode != "OBJECT":
            bpy.ops.object.mode_set(mode="OBJECT")

        snap.ensure_cycles(opt["compute_device"])
        dups = _duplicate_target_objects(context, sources, opt["apply_modifiers"], tracker)
        if not dups:
            return False, "ポリゴンを持つ有効なオブジェクトがありません。"

        for dup_obj, used_mat_indices in dups:
            _bake_single_object(context, dup_obj, used_mat_indices, opt, tracker)

        bpy.ops.object.select_all(action="DESELECT")
        for dup_obj, _ in dups:
            dup_obj.select_set(True)
        context.view_layer.objects.active = dups[0][0]

        gltf_props = bpy.ops.export_scene.gltf.get_rna_type().properties
        raw_export_kwargs = {
            "filepath": filepath,
            "export_format": "GLB",
            "use_selection": True,
            "export_apply": True,
            "export_texcoords": True,
            "export_normals": True,
            "export_materials": "EXPORT",
            "export_image_format": opt["image_format"],
            "export_yup": True,
            "export_draco_mesh_compression_enable": False,
        }
        export_kwargs = {k: v for k, v in raw_export_kwargs.items() if k in gltf_props}
        bpy.ops.export_scene.gltf(**export_kwargs)

        return True, f"{len(dups)} 個のオブジェクトを自動ベイクしてGLB書き出ししました。"
    finally:
        tracker.cleanup()
        snap.restore()


class YMM4D3D11ExportSettings(bpy.types.PropertyGroup):
    target_mode: EnumProperty(
        name="対象オブジェクト",
        items=[
            ("SELECTED", "選択オブジェクトのみ", "現在選択されているメッシュ類のみをベイクして書き出します"),
            ("VISIBLE", "表示中の全メッシュ", "ビュー上で表示されている全メッシュをまとめて書き出します"),
        ],
        default="SELECTED",
    )
    texture_size: EnumProperty(
        name="テクスチャ解像度",
        items=[
            ("512", "512 x 512 (軽量)", "小物・テスト向け"),
            ("1024", "1024 x 1024 (標準)", "標準的な品質と容量のバランス"),
            ("2048", "2048 x 2048 (高画質)", "メインモデル・アップ向け"),
            ("4096", "4096 x 4096 (最高画質)", "細密テクスチャ向け（処理時間増加）"),
        ],
        default="1024",
    )
    uv_mode: EnumProperty(
        name="UV展開モード",
        items=[
            ("ALWAYS_NEW", "常に新規ベイクUV生成（推奨）", "既存UVテクスチャは維持参照しつつ重複のないベイク専用UVを新規生成します"),
            ("AUTO", "UV未展開のみ自動展開", "UVマップが0個のメッシュのみ自動でスマートUV投影します"),
            ("KEEP", "既存UVをそのまま使用", "元のUVマップでベイクします（UV無しは自動生成）"),
        ],
        default="ALWAYS_NEW",
    )
    uv_angle_limit: FloatProperty(
        name="スマートUV角度制限 (°)",
        default=66.0,
        min=1.0,
        max=89.0,
    )
    uv_island_margin: FloatProperty(
        name="UVアイランド余白",
        default=0.02,
        min=0.0,
        max=0.2,
    )
    bake_margin: IntProperty(
        name="ベイク余白 (px)",
        default=8,
        min=0,
        max=64,
    )
    bake_samples: IntProperty(
        name="Cyclesサンプル数",
        default=8,
        min=1,
        max=64,
        description="法線ベイク時のサンプル数（Emit系は自動で1〜4サンプルを使用）",
    )
    compute_device: EnumProperty(
        name="計算デバイス",
        items=[
            ("KEEP", "元設定を維持", "現在のCyclesデバイス設定をそのまま使用"),
            ("CPU", "CPU（安定・確実）", "VRAM容量に依存せず安定してベイクします"),
            ("GPU", "GPU（高速）", "対応GPUで高速にベイクします"),
        ],
        default="KEEP",
    )
    image_format: EnumProperty(
        name="画像形式",
        items=[
            ("AUTO", "PNG（標準・劣化なし）", "透過・ORMを含めロスレスPNGで格納"),
            ("JPEG", "JPEG（容量削減）", "非透過テクスチャをJPEG圧縮で軽量化"),
        ],
        default="AUTO",
    )
    apply_modifiers: BoolProperty(
        name="モディファイア/ジオメトリノード適用",
        default=True,
    )
    bake_base_color: BoolProperty(
        name="Base Color (アルベド)",
        default=True,
    )
    bake_orm: BoolProperty(
        name="Roughness / Metallic (ORM)",
        default=True,
    )
    bake_normal: BoolProperty(
        name="Normal / Bump (法線マップ)",
        default=True,
    )
    bake_emission: BoolProperty(
        name="Emission (発光・HDR強度)",
        default=True,
    )
    bake_alpha: BoolProperty(
        name="Alpha (透過・マスク)",
        default=True,
    )
    bake_transmission: BoolProperty(
        name="Transmission / IOR (ガラス屈折)",
        default=True,
    )

    def to_options_dict(self):
        return {
            "target_mode": self.target_mode,
            "texture_size": self.texture_size,
            "uv_mode": self.uv_mode,
            "uv_angle_limit": self.uv_angle_limit,
            "uv_island_margin": self.uv_island_margin,
            "bake_margin": self.bake_margin,
            "bake_samples": self.bake_samples,
            "compute_device": self.compute_device,
            "image_format": self.image_format,
            "apply_modifiers": self.apply_modifiers,
            "bake_base_color": self.bake_base_color,
            "bake_orm": self.bake_orm,
            "bake_normal": self.bake_normal,
            "bake_emission": self.bake_emission,
            "bake_alpha": self.bake_alpha,
            "bake_transmission": self.bake_transmission,
        }


class EXPORT_SCENE_OT_ymm4_d3d11_glb(bpy.types.Operator, ExportHelper):
    """元のシーンやマテリアルを変更せずに、複製上でUV展開＆Cycles自動ベイクしてYMM4用GLBを書き出します"""

    bl_idname = "export_scene.ymm4_d3d11_glb"
    bl_label = "YMM4 D3D11 GLB 書き出し"
    bl_options = {"PRESET"}

    filename_ext = ".glb"
    filter_glob: StringProperty(default="*.glb", options={"HIDDEN"})

    target_mode: EnumProperty(
        name="対象オブジェクト",
        items=[
            ("SELECTED", "選択オブジェクトのみ", ""),
            ("VISIBLE", "表示中の全メッシュ", ""),
        ],
        default="SELECTED",
    )
    texture_size: EnumProperty(
        name="テクスチャ解像度",
        items=[
            ("512", "512 x 512", ""),
            ("1024", "1024 x 1024", ""),
            ("2048", "2048 x 2048", ""),
            ("4096", "4096 x 4096", ""),
        ],
        default="1024",
    )
    uv_mode: EnumProperty(
        name="UV展開モード",
        items=[
            ("ALWAYS_NEW", "常に新規ベイクUV生成（推奨）", ""),
            ("AUTO", "UV未展開のみ自動展開", ""),
            ("KEEP", "既存UVをそのまま使用", ""),
        ],
        default="ALWAYS_NEW",
    )
    uv_angle_limit: FloatProperty(name="スマートUV角度 (°)", default=66.0, min=1.0, max=89.0)
    uv_island_margin: FloatProperty(name="UVアイランド余白", default=0.02, min=0.0, max=0.2)
    bake_margin: IntProperty(name="ベイク余白 (px)", default=8, min=0, max=64)
    bake_samples: IntProperty(name="サンプル数", default=8, min=1, max=64)
    compute_device: EnumProperty(
        name="計算デバイス",
        items=[("KEEP", "元設定を維持", ""), ("CPU", "CPU", ""), ("GPU", "GPU", "")],
        default="KEEP",
    )
    image_format: EnumProperty(
        name="画像形式",
        items=[("AUTO", "PNG（標準）", ""), ("JPEG", "JPEG（軽量）", "")],
        default="AUTO",
    )
    apply_modifiers: BoolProperty(name="モディファイア適用", default=True)
    bake_base_color: BoolProperty(name="Base Color", default=True)
    bake_orm: BoolProperty(name="Roughness / Metallic", default=True)
    bake_normal: BoolProperty(name="Normal / Bump", default=True)
    bake_emission: BoolProperty(name="Emission", default=True)
    bake_alpha: BoolProperty(name="Alpha", default=True)
    bake_transmission: BoolProperty(name="Transmission (ガラス)", default=True)

    def invoke(self, context, event):
        if hasattr(context.scene, "ymm4_d3d11_export"):
            s = context.scene.ymm4_d3d11_export
            for k in s.to_options_dict().keys():
                if hasattr(self, k):
                    setattr(self, k, getattr(s, k))
        if not self.filepath:
            blend_name = os.path.splitext(os.path.basename(bpy.data.filepath))[0] if bpy.data.filepath else "ymm4_model"
            self.filepath = blend_name + ".glb"
        return super().invoke(context, event)

    def draw(self, context):
        layout = self.layout
        box = layout.box()
        box.label(text="基本設定", icon="EXPORT")
        box.prop(self, "target_mode")
        box.prop(self, "texture_size")
        box.prop(self, "uv_mode")
        box.prop(self, "apply_modifiers")
        box.prop(self, "image_format")

        box2 = layout.box()
        box2.label(text="ベイク出力項目", icon="SHADING_RENDERED")
        box2.prop(self, "bake_base_color")
        box2.prop(self, "bake_orm")
        box2.prop(self, "bake_normal")
        box2.prop(self, "bake_emission")
        box2.prop(self, "bake_alpha")
        box2.prop(self, "bake_transmission")

        box3 = layout.box()
        box3.label(text="詳細・品質", icon="SETTINGS")
        box3.prop(self, "compute_device")
        box3.prop(self, "bake_samples")
        box3.prop(self, "bake_margin")
        box3.prop(self, "uv_angle_limit")
        box3.prop(self, "uv_island_margin")

    def execute(self, context):
        opt = {
            "target_mode": self.target_mode,
            "texture_size": self.texture_size,
            "uv_mode": self.uv_mode,
            "uv_angle_limit": self.uv_angle_limit,
            "uv_island_margin": self.uv_island_margin,
            "bake_margin": self.bake_margin,
            "bake_samples": self.bake_samples,
            "compute_device": self.compute_device,
            "image_format": self.image_format,
            "apply_modifiers": self.apply_modifiers,
            "bake_base_color": self.bake_base_color,
            "bake_orm": self.bake_orm,
            "bake_normal": self.bake_normal,
            "bake_emission": self.bake_emission,
            "bake_alpha": self.bake_alpha,
            "bake_transmission": self.bake_transmission,
        }
        try:
            ok, msg = run_bake_and_export(context, self.filepath, opt)
            if not ok:
                self.report({"ERROR"}, msg)
                return {"CANCELLED"}
            self.report({"INFO"}, msg)
            return {"FINISHED"}
        except Exception as ex:
            self.report({"ERROR"}, f"GLB書き出し中にエラーが発生しました: {ex}")
            return {"CANCELLED"}


class VIEW3D_PT_ymm4_d3d11_exporter(bpy.types.Panel):
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "YMM4 D3D11"
    bl_label = "YMM4 D3D11 GLB 自動ベイク"

    def draw(self, context):
        layout = self.layout
        s = context.scene.ymm4_d3d11_export

        info = layout.box()
        info.label(text="v1.2.0 非破壊ベイク & ガラス屈折対応", icon="INFO")
        col_i = info.column(align=True)
        col_i.scale_y = 0.85
        col_i.label(text="元のオブジェクト・マテリアル・")
        col_i.label(text="レンダー設定は一切変更しません。")

        col = layout.column(align=True)
        col.prop(s, "target_mode")
        col.prop(s, "texture_size")
        col.prop(s, "uv_mode")
        col.prop(s, "apply_modifiers")
        col.prop(s, "image_format")

        box = layout.box()
        box.label(text="ベイクするPBRマップ", icon="NODE_MATERIAL")
        box.prop(s, "bake_base_color")
        box.prop(s, "bake_orm")
        box.prop(s, "bake_normal")
        box.prop(s, "bake_emission")
        box.prop(s, "bake_alpha")
        box.prop(s, "bake_transmission")

        box2 = layout.box()
        box2.label(text="Cycles / UV 詳細設定", icon="TOOL_SETTINGS")
        box2.prop(s, "compute_device")
        box2.prop(s, "bake_samples")
        box2.prop(s, "bake_margin")
        box2.prop(s, "uv_angle_limit")
        box2.prop(s, "uv_island_margin")

        row = layout.row()
        row.scale_y = 1.45
        row.operator("export_scene.ymm4_d3d11_glb", text="自動ベイクして GLB 出力", icon="EXPORT")


def menu_func_export(self, context):
    self.layout.operator(EXPORT_SCENE_OT_ymm4_d3d11_glb.bl_idname, text="YMM4 D3D11 自動ベイク (.glb)")


classes = (
    YMM4D3D11ExportSettings,
    EXPORT_SCENE_OT_ymm4_d3d11_glb,
    VIEW3D_PT_ymm4_d3d11_exporter,
)


def register():
    for cls in classes:
        try:
            bpy.utils.register_class(cls)
        except ValueError:
            pass
    bpy.types.Scene.ymm4_d3d11_export = PointerProperty(type=YMM4D3D11ExportSettings)
    try:
        bpy.types.TOPBAR_MT_file_export.remove(menu_func_export)
    except Exception:
        pass
    bpy.types.TOPBAR_MT_file_export.append(menu_func_export)


def unregister():
    try:
        bpy.types.TOPBAR_MT_file_export.remove(menu_func_export)
    except Exception:
        pass
    if hasattr(bpy.types.Scene, "ymm4_d3d11_export"):
        del bpy.types.Scene.ymm4_d3d11_export
    for cls in reversed(classes):
        try:
            bpy.utils.unregister_class(cls)
        except RuntimeError:
            pass


if __name__ == "__main__":
    register()
