"""Bounded reconstruction of the real terminal renderer used by explicit acceptance lanes."""
import re


ANSI = re.compile(r"(\x1b\[[0-?]*[ -/]*[@-~]|\x1b.)")


def raw_terminal_text(traffic):
    return re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", traffic.decode("utf-8", errors="replace"))


def terminal_text(traffic, *, history=False, rows=100, columns=160):
    """Recover spaces omitted by per-cell cursor positioning, without growing a screen from escape input."""
    if len(traffic) > 4 * 1024 * 1024:
        raise RuntimeError("Disposable terminal output exceeded its bound")
    cells, frames = {}, []
    row = column = 0

    def screen():
        return " ".join(" ".join("".join(cells.get((y, x), " ") for x in range(columns)) for y in range(rows)).split())

    def snapshot():
        if history:
            text = screen()
            if text and (not frames or frames[-1] != text):
                frames.append(text)
                del frames[:-64]

    for token in ANSI.split(traffic.decode("utf-8", errors="replace")):
        if token.startswith("\x1b["):
            if token[-1] in "Hf":
                coordinates = token[2:-1].split(";")
                if all(value == "" or value.isdigit() for value in coordinates):
                    row = int(coordinates[0] or "1") - 1
                    column = int(coordinates[1] or "1") - 1 if len(coordinates) > 1 else 0
            elif token in ("\x1b[2J", "\x1b[3J"):
                snapshot()
                cells.clear()
            elif token in ("\x1b[0m", "\x1b[?1049h", "\x1b[?1049l"):
                snapshot()
            continue
        if token.startswith("\x1b"):
            continue
        for character in token:
            if character == "\r":
                column = 0
            elif character == "\n":
                row += 1
            elif character >= " ":
                if 0 <= row < rows and 0 <= column < columns:
                    cells[row, column] = character
                column += 1
    current = screen()
    # Plain command output and historical logical frames remain available after
    # a wizard exits its alternate screen. Secret assertions use the full raw text.
    return "\n".join([*frames, current, raw_terminal_text(traffic)]) if history else current
