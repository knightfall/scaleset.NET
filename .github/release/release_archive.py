"""Archive the complete verified evidence deterministically, including dotfiles."""
from pathlib import Path
import sys
import zipfile


def create_archive(root, destination):
    root = Path(root).resolve(strict=True)
    with zipfile.ZipFile(destination, "x", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(root.rglob("*")):
            if path.is_symlink():
                raise ValueError("Symbolic links are not release evidence")
            if path.is_file():
                info = zipfile.ZipInfo(path.relative_to(root).as_posix(), date_time=(1980, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(info, path.read_bytes())


if __name__ == "__main__":
    create_archive(sys.argv[1], sys.argv[2])
