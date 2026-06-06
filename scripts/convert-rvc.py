#!/usr/bin/env python3
"""
convert-rvc.py — Convert an RVC .pth model to .onnx for use with Talktastic.

Usage:
    python convert-rvc.py model.pth [--output model.onnx]

Requirements:
    pip install torch onnx

The script auto-detects the model architecture from the checkpoint config
and exports it using TorchScript-based ONNX export. Config metadata is
embedded in the output so Talktastic can read the target sample rate.
"""

import argparse
import json
import os
import sys


def find_model_class(config):
    """Determine the correct model class based on config length and version."""
    # Try to import from rvc-onnx's bundled model definitions first,
    # then fall back to RVC-Project's infer_pack
    model_modules = []

    try:
        from infer.lib.infer_pack.models_onnx import SynthesizerTrnMsNSFsidM
        model_modules.append(("SynthesizerTrnMsNSFsidM", SynthesizerTrnMsNSFsidM))
    except ImportError:
        pass

    try:
        from infer.lib.infer_pack.models import SynthesizerTrnMs768NSFsid
        model_modules.append(("SynthesizerTrnMs768NSFsid", SynthesizerTrnMs768NSFsid))
    except ImportError:
        pass

    try:
        from infer.lib.infer_pack.models import SynthesizerTrnMs256NSFsid
        model_modules.append(("SynthesizerTrnMs256NSFsid", SynthesizerTrnMs256NSFsid))
    except ImportError:
        pass

    if not model_modules:
        print("ERROR: Could not import any RVC model classes.", file=sys.stderr)
        print("Make sure you have the RVC model definitions available.", file=sys.stderr)
        print("Clone https://github.com/dev6699/rvc-onnx and add it to sys.path.", file=sys.stderr)
        sys.exit(1)

    return model_modules


def main():
    parser = argparse.ArgumentParser(description="Convert RVC .pth to .onnx")
    parser.add_argument("input", help="Path to .pth checkpoint file")
    parser.add_argument("--output", "-o", help="Output .onnx path (default: same name as input)")
    parser.add_argument("--fp32", action="store_true", help="Export in FP32 instead of FP16")
    parser.add_argument(
        "--rvc-path",
        help="Path to rvc-onnx or RVC-Project repo (for model definitions)",
    )
    args = parser.parse_args()

    if args.rvc_path:
        sys.path.insert(0, args.rvc_path)

    import torch

    # Load checkpoint
    print(f"Loading {args.input}...")
    ckpt = torch.load(args.input, map_location="cpu", weights_only=False)

    config = ckpt.get("config")
    version = ckpt.get("version", "v2")
    sr = ckpt.get("sr", "unknown")
    f0 = ckpt.get("f0", 1)
    info = ckpt.get("info", "")

    if config is None:
        print("ERROR: Checkpoint has no 'config' key. Not an RVC model?", file=sys.stderr)
        sys.exit(1)

    print(f"  Config ({len(config)} params): {config}")
    print(f"  Version: {version}, SR: {sr}, F0: {f0}")
    if info:
        print(f"  Info: {info}")

    tgt_sr = int(config[-1])
    is_half = not args.fp32

    # Try each model class until one works
    model_classes = find_model_class(config)
    model = None

    for class_name, cls in model_classes:
        try:
            model = cls(*config, is_half=is_half, version=version)
            print(f"  Using model class: {class_name}")
            break
        except TypeError:
            try:
                model = cls(*config, is_half=is_half)
                print(f"  Using model class: {class_name} (without version)")
                break
            except TypeError:
                continue

    if model is None:
        # Last resort: try without is_half
        for class_name, cls in model_classes:
            try:
                model = cls(*config, version=version)
                print(f"  Using model class: {class_name} (without is_half)")
                break
            except TypeError:
                try:
                    model = cls(*config)
                    print(f"  Using model class: {class_name} (minimal args)")
                    break
                except TypeError:
                    continue

    if model is None:
        print("ERROR: Could not instantiate any model class with the given config.", file=sys.stderr)
        sys.exit(1)

    # Load weights (exclude encoder_q which isn't needed for inference)
    state = {k: v for k, v in ckpt["weight"].items() if "enc_q" not in k}
    model.load_state_dict(state, strict=False)
    model.eval()

    if is_half:
        model.half()

    # Prepare test tensors
    T = 100
    dtype = torch.float16 if is_half else torch.float32
    phone = torch.randn(1, T, 768, dtype=dtype)
    phone_lengths = torch.tensor([T], dtype=torch.int64)
    pitch = torch.randint(1, 255, (1, T), dtype=torch.int64)
    pitchf = torch.randn(1, T, dtype=dtype)
    ds = torch.zeros(1, dtype=torch.int64)
    rnd = torch.randn(1, 192, T, dtype=dtype)

    # Export
    out_path = args.output or args.input.replace(".pth", ".onnx")
    print(f"Exporting to {out_path}...")

    torch.onnx.export(
        model,
        (phone, phone_lengths, pitch, pitchf, ds, rnd),
        out_path,
        input_names=["phone", "phone_lengths", "pitch", "pitchf", "ds", "rnd"],
        output_names=["audio"],
        dynamic_axes={
            "phone": {1: "seq_len"},
            "pitch": {1: "seq_len"},
            "pitchf": {1: "seq_len"},
            "rnd": {2: "seq_len"},
        },
        opset_version=17,
        dynamo=False,
    )

    # Add metadata
    import onnx

    model_onnx = onnx.load(out_path)
    meta = model_onnx.metadata_props.add()
    meta.key = "config"
    meta.value = json.dumps(list(config))
    onnx.save(model_onnx, out_path)

    size_mb = os.path.getsize(out_path) / 1024 / 1024
    print(f"Done! {out_path} ({size_mb:.1f} MB, target SR: {tgt_sr} Hz)")


if __name__ == "__main__":
    main()
