from __future__ import annotations

import site
import sys
from pathlib import Path


def replace_once(path: Path, old: str, new: str) -> bool:
    text = path.read_text(encoding="utf-8")
    if new in text:
        return False
    if old not in text:
        raise RuntimeError(f"Could not find expected snippet in {path}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")
    return True


def insert_after(path: Path, needle: str, insert_text: str) -> bool:
    text = path.read_text(encoding="utf-8")
    if insert_text in text:
        return False
    if needle not in text:
        raise RuntimeError(f"Could not find expected insertion point in {path}")
    path.write_text(text.replace(needle, needle + insert_text, 1), encoding="utf-8")
    return True


def resolve_site_packages() -> Path:
    for candidate in site.getsitepackages():
        path = Path(candidate)
        if path.name == "site-packages" and path.exists():
            return path
    raise RuntimeError("Could not locate site-packages for the current interpreter")


def main() -> int:
    site_packages = resolve_site_packages()

    tracker_utils = site_packages / "sam3" / "model" / "sam3_tracker_utils.py"
    connected_components = site_packages / "sam3" / "perflib" / "connected_components.py"
    nms = site_packages / "sam3" / "perflib" / "nms.py"

    replace_once(
        tracker_utils,
        "from sam3.model.edt import edt_triton\n",
        """try:\n    from sam3.model.edt import edt_triton\nexcept ModuleNotFoundError as exc:\n    # Triton is not currently available via a supported Windows wheel for this setup.\n    # Fall back to the slower OpenCV-based implementation that already exists below.\n    if exc.name not in {\"triton\", \"triton.language\"}:\n        raise\n    edt_triton = None\n""",
    )
    replace_once(
        tracker_utils,
        """    fn_mask_dt = edt_triton(padded_fn_masks)\n    fp_mask_dt = edt_triton(padded_fp_masks)\n""",
        """    if edt_triton is None:\n        return sample_one_point_from_error_center_slow(\n            gt_masks,\n            pred_masks,\n            padding=padding,\n        )\n\n    fn_mask_dt = edt_triton(padded_fn_masks)\n    fp_mask_dt = edt_triton(padded_fp_masks)\n""",
    )

    insert_after(
        connected_components,
        "    batch_size = input_tensor.shape[0]\n",
        "    if batch_size == 0:\n        empty = torch.zeros_like(input_tensor, dtype=torch.int64)\n        return empty.view(out_shape), empty.view(out_shape)\n\n",
    )
    replace_once(
        connected_components,
        """        else:\n            # triton fallback\n            from sam3.perflib.triton.connected_components import (\n                connected_components_triton,\n            )\n\n            return connected_components_triton(input_tensor)\n""",
        """        else:\n            try:\n                from sam3.perflib.triton.connected_components import (\n                    connected_components_triton,\n                )\n            except ModuleNotFoundError:\n                labels_cpu, counts_cpu = connected_components_cpu(input_tensor.cpu())\n                return (\n                    labels_cpu.to(input_tensor.device),\n                    counts_cpu.to(input_tensor.device),\n                )\n\n            return connected_components_triton(input_tensor)\n""",
    )

    replace_once(
        nms,
        """        else:\n            from sam3.perflib.triton.nms import nms_triton\n\n            return nms_triton(ious, scores, iou_threshold)\n""",
        """        else:\n            try:\n                from sam3.perflib.triton.nms import nms_triton\n            except ModuleNotFoundError:\n                return generic_nms_cpu(\n                    ious.cpu(),\n                    scores.cpu(),\n                    iou_threshold,\n                ).to(scores.device)\n\n            return nms_triton(ious, scores, iou_threshold)\n""",
    )

    print("Applied Windows SAM3 fallback patches.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
