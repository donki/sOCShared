"""Cobertura de toda la app con la medida comun (constitucion General 8.6, 2026-11-25).

Solo cuenta codigo ejecutable: las lineas que coverlet marca como puntos de secuencia. Lo que no se
compila en las pruebas (codigo de otra plataforma o del dispositivo, bloques #if que las pruebas no
activan) se estima con las mismas reglas: sentencias, cuerpos de metodos y accesores; fuera XAML,
interfaces, enums, declaraciones sin cuerpo, using, namespace, atributos, comentarios y generados.

Uso:
  python cobertura-app.py --app <carpeta del repo> --report coverage1.xml [--report coverage2.xml]
                          [--defines WINDOWS] [--calibrar]

--report  informes cobertura de coverlet (se juntan: una linea esta cubierta si lo esta en alguno).
--defines simbolos que tenia activos el informe correspondiente (uno por --report, separados por
          comas; vacio = net10.0 sin plataforma). Sirve para saber que bloques #if se compilaron.
--calibrar compara la estimacion con coverlet en los ficheros instrumentados.
"""
import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET

PLATFORMS = {"WINDOWS", "ANDROID", "IOS", "MACCATALYST", "TIZEN"}
EXCLUDED_DIRS = re.compile(r"[\\/](obj|bin|constitution|node_modules|[^\\/]+\.[^\\/]*Tests|TestResults)[\\/]", re.I)
GENERATED = re.compile(r"\.(g|g\.i|Designer|AssemblyInfo)\.cs$", re.I)
PLATFORM_DIR = re.compile(r"[\\/]Platforms[\\/](Windows|Android|iOS|MacCatalyst|Tizen)[\\/]", re.I)


def norm(path):
    return os.path.normcase(os.path.abspath(path))


def eval_condition(expr, symbols):
    """Evalua la condicion de un #if con los simbolos dados (los desconocidos, falsos salvo DEBUG)."""
    py = expr.replace("&&", " and ").replace("||", " or ")
    py = re.sub(r"!(?!=)", " not ", py)

    def sym(m):
        name = m.group(0)
        if name in ("and", "or", "not", "True", "False"):
            return name
        if name == "true":
            return "True"
        if name == "false":
            return "False"
        return "True" if (name in symbols or name == "DEBUG") else "False"

    py = re.sub(r"[A-Za-z_][A-Za-z0-9_]*", sym, py)
    try:
        return bool(eval(py, {"__builtins__": {}}))
    except Exception:
        return False


def strip_comments(lines):
    """Devuelve las lineas sin comentarios (// y /* */) y con el contenido de cadenas y caracteres
    vaciado (se quedan las comillas), para que una URL o una llave dentro de un texto no confundan."""
    out = []
    mode = None  # None, 'block' (/* */), 'verbatim' (@"..."), 'raw' (""" ... """)
    raw_quotes = 0
    for line in lines:
        s, i, res = line, 0, []
        while i < len(s):
            if mode == "block":
                j = s.find("*/", i)
                if j < 0:
                    i = len(s)
                else:
                    mode, i = None, j + 2
                continue
            if mode == "verbatim":
                if s[i] == '"':
                    if s.startswith('""', i):
                        i += 2
                        continue
                    res.append('"')
                    mode, i = None, i + 1
                    continue
                i += 1
                continue
            if mode == "raw":
                if s.startswith('"' * raw_quotes, i):
                    res.append('"')
                    mode, i = None, i + raw_quotes
                    continue
                i += 1
                continue
            c = s[i]
            if s.startswith("/*", i):
                mode, i = "block", i + 2
                continue
            if s.startswith("//", i):
                break
            m = re.match(r'[$@]*("{3,})', s[i:])
            if m and (s[i] in '$"'):
                raw_quotes = len(m.group(1))
                res.append('"')
                mode, i = "raw", i + m.end()
                continue
            if s.startswith('@"', i) or s.startswith('$@"', i) or s.startswith('@$"', i):
                res.append('"')
                mode, i = "verbatim", i + s[i:].index('"') + 1
                continue
            if c == '"' or s.startswith('$"', i):
                i += 2 if c == "$" else 1
                while i < len(s) and s[i] != '"':
                    i += 2 if s[i] == "\\" else 1
                res.append('""')
                i += 1
                continue
            if c == "'":
                m = re.match(r"'(\\.[^']*|[^'\\])'", s[i:])
                if m:
                    res.append("' '")
                    i += m.end()
                    continue
            res.append(c)
            i += 1
        out.append("".join(res).rstrip())
    return out


DECL = re.compile(
    r"^((public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|new|file|ref)\s+)*"
    r"(class|struct|record|interface|enum|namespace)\b\s*(struct\s+|class\s+)?([A-Za-z_][\w.]*)?")
LOCAL_DECL = re.compile(r"^(?!return\b|break\b|continue\b|throw\b|yield\b|await\b)[\w<>\[\],.?()]+\s+\w+(\s*,\s*\w+)*;$")
BODY_SKIP = re.compile(r"^(else|try|finally|do|checked|unchecked|unsafe)$|^(case\b.*|default):$")


def estimate(lines, active_of):
    """Lineas que serian puntos de secuencia (Debug), solo en las lineas para las que active_of(n) es True.

    Reglas calibradas contra coverlet (que marca todas las lineas de cada punto de secuencia):
    - Dentro de un cuerpo (metodo, accesor, lambda) cuenta todo salvo else/try/finally/do sueltos,
      etiquetas case/default y declaraciones locales sin valor.
    - A nivel de tipo cuentan los inicializadores de campos y propiedades y los miembros de expresion
      (=>) en todas sus lineas, la firma de los constructores (llamada a la base), los tipos con
      constructor primario y las llaves de los cuerpos. No cuentan firmas de metodos y propiedades,
      propiedades automaticas, atributos, declaraciones de tipos ni cuerpos de interfaces y enums.
    """
    code = strip_comments(lines)
    result = set()
    stack = []          # ('ns'|'type'|'iface'|'acc'|'body', nombre del tipo)
    pending = None      # lo que abrira la siguiente '{'
    pending_name = None
    member_open = False  # inicializador o expresion de miembro que sigue en la linea siguiente
    member_depth = 0
    header = []          # lineas de la cabecera del miembro actual (nivel de tipo)
    ctor = False

    def top():
        return stack[-1][0] if stack else "ns"

    def type_name():
        for k, nm in reversed(stack):
            if k == "type":
                return nm
        return None

    last_body = ""
    attr_depth = 0
    prev_open = False  # la ultima linea contada deja la sentencia abierta (coverlet marca el tramo entero)
    for idx, text in enumerate(code):
        n = idx + 1
        t = text.strip()
        if not t or t.startswith("#"):
            if prev_open and not t.startswith("#") and active_of(n):
                result.add(n)
            continue
        ctx = top()
        counts = False
        # Atributo de varias lineas ([Activity(\n Theme = ...,\n ...)]): no es codigo.
        if attr_depth > 0 or (ctx != "body" and not member_open and t.startswith("[") and t.count("[") > t.count("]")):
            attr_depth += t.count("[") - t.count("]")
            continue
        if ctx not in ("body",) and not member_open and t.startswith("[") and "]" in t:
            t = re.sub(r"^(\[[^\]]*\]\s*)+", "", t)  # atributos delante del miembro en la misma linea
            if not t:
                continue
        if ctx == "body" or member_open:
            if member_open and ctx != "body":
                counts = True
            elif BODY_SKIP.match(t) or LOCAL_DECL.match(t) or t.startswith("const "):
                counts = False
            elif t.startswith("}") and re.match(r"^(return|throw|break|continue|goto)" + chr(92) + "b", last_body)                     and sum(1 for k, _ in stack if k == "body") > 1:
                counts = False  # cierre que no se alcanza
            else:
                counts = True
        elif ctx == "iface":
            pass
        elif ctx == "acc":
            if t in ("{", "}"):
                counts = False
            elif re.match(r"^((private|protected|internal|public)\s+)*(get|set|init|add|remove)\s*;", t):
                counts = True  # accesor automatico
            elif "=>" in t or "{" in t:
                counts = True
                if not t.rstrip().endswith((";", "}")):
                    member_open, member_depth = True, len(stack)
            else:
                pending = "body"
        else:  # nivel de tipo o de espacio de nombres
            m = DECL.match(t)
            if m and not t.startswith("using"):
                kind = m.group(3)
                pending = {"interface": "iface", "enum": "iface", "namespace": "ns"}.get(kind, "type")
                pending_name = m.group(5)
                if kind in ("class", "struct", "record") and "(" in t:
                    counts = True  # constructor primario
                if t.endswith(";") and kind != "record":
                    pending = None
                if t.endswith(";"):
                    pending = None
                header = []
            elif t.startswith("using ") or t.startswith("[") or t.startswith("extern alias"):
                pass
            elif t == "{":
                pass  # se resuelve abajo
            elif t == "}" or t == "};":
                pass
            else:
                header.append(n)
                joined = " ".join(code[h - 1].strip() for h in header)
                nm = type_name()
                if nm and re.search(r"(^|\s)" + re.escape(nm) + r"\s*\(", joined) and "=" not in joined.split("(")[0]:
                    ctor = True
                eq = re.search(r"(?<![=!<>])=(?![=>])", joined)
                par = joined.find("(")
                if "=>" in t:
                    if t.endswith("=>"):
                        member_open, member_depth = True, len(stack)
                    else:
                        counts = True
                        member_open, member_depth = (not t.endswith((";", "}"))), len(stack)
                    header = []
                elif eq and (par < 0 or eq.start() < par):
                    counts = not re.search(r"(^|\s)const\s", joined)
                    member_open, member_depth = (not t.endswith(";")), len(stack)
                    header = []
                elif t.endswith(";"):
                    header = []
                elif ctor:
                    counts = True
                    pending = "body"
                else:
                    pending = "acc" if par < 0 else "body"
                    if t.endswith("}") and "{" in t:
                        header = []  # miembro de una linea: propiedad automatica o metodo vacio
                        pending = None
                        counts = True
        # Llaves.
        for ch in t:
            if ch == "{":
                ctx_now = top()
                if ctx_now in ("body",) or (member_open and ctx_now != "acc"):
                    stack.append(("body", None))
                elif ctx_now == "iface":
                    stack.append(("iface", None))
                elif pending in ("type", "iface", "ns"):
                    stack.append((pending, pending_name))
                elif pending == "acc":
                    stack.append(("acc", None))
                elif pending == "body" or ctx_now == "acc":
                    stack.append(("body", None))
                    if t.startswith("{") and ctx_now != "body":
                        counts = True
                else:
                    stack.append(("body", None))
                pending, header, ctor = None, [], False
            elif ch == "}":
                if stack:
                    stack.pop()
        if member_open and t.endswith(";") and len(stack) <= member_depth:
            member_open = False
        if ctx == "body":
            last_body = t
        prev_open = counts and not t.endswith((";", "{", "}", ":", ")"))
        if counts and active_of(n):
            result.add(n)
    return result


def active_lines(lines, symbols):
    """Para cada linea, si se compila con esos simbolos (bloques #if / #elif / #else)."""
    active, stack = [], []
    for raw in lines:
        t = raw.strip()
        cur = all(s[0] for s in stack)
        if t.startswith("#if "):
            v = eval_condition(t[4:], symbols)
            stack.append([v, v])
            active.append(False)
            continue
        if t.startswith("#elif "):
            if stack:
                v = (not stack[-1][1]) and eval_condition(t[6:], symbols)
                stack[-1] = [v, stack[-1][1] or v]
            active.append(False)
            continue
        if t.startswith("#else"):
            if stack:
                stack[-1] = [not stack[-1][1], True]
            active.append(False)
            continue
        if t.startswith("#endif"):
            if stack:
                stack.pop()
            active.append(False)
            continue
        active.append(cur)
    return active


def app_files(app):
    for root, dirs, files in os.walk(app):
        for f in files:
            if not f.endswith(".cs"):
                continue
            p = os.path.join(root, f)
            rel = p[len(app):]
            if EXCLUDED_DIRS.search(rel + os.sep) or EXCLUDED_DIRS.search(rel) or GENERATED.search(f):
                continue
            yield p


def load_report(path):
    tree = ET.parse(path)
    root = tree.getroot()
    sources = [s.text for s in root.iter("source") if s.text]
    lines = {}
    for cls in root.iter("class"):
        fn = cls.get("filename")
        cand = [fn] if os.path.isabs(fn) else [os.path.join(s, fn) for s in sources]
        full = next((c for c in cand if os.path.exists(c)), None)
        if not full:
            continue
        d = lines.setdefault(norm(full), {})
        for ln in cls.iter("line"):
            k = int(ln.get("number"))
            d[k] = max(d.get(k, 0), int(ln.get("hits")))
    return lines


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--app", required=True)
    ap.add_argument("--report", action="append", default=[])
    ap.add_argument("--defines", action="append", default=[])
    ap.add_argument("--calibrar", action="store_true")
    ap.add_argument("--detalle", action="store_true", help="cifras por fichero")
    a = ap.parse_args()
    app = norm(a.app)
    reports = [load_report(r) for r in a.report]
    defines = [set(filter(None, (a.defines[i] if i < len(a.defines) else "").split(","))) for i in range(len(reports))]

    total = covered = 0
    calib = [0, 0, 0]  # coincidencias, solo estimadas, solo coverlet
    rows = []
    for f in sorted(app_files(app)):
        nf = norm(f)
        with open(f, encoding="utf-8-sig", errors="replace") as h:
            lines = h.read().split("\n")
        executable, hit = set(), set()
        for rep, syms in zip(reports, defines):
            if nf not in rep:
                continue
            act = active_lines(lines, syms)
            executable |= set(rep[nf])
            hit |= {k for k, v in rep[nf].items() if v > 0}
            if a.calibrar:
                est = estimate(lines, lambda n, act=act: act[n - 1] if n - 1 < len(act) else False)
                cov = set(rep[nf])
                calib[0] += len(est & cov)
                calib[1] += len(est - cov)
                calib[2] += len(cov - est)
                if a.detalle and est != cov:
                    print(f"  {os.path.relpath(f, app)}: estimadas de mas {sorted(est - cov)[:12]} de menos {sorted(cov - est)[:12]}")
        # Lo que ningun informe compilo: el fichero entero o los bloques #if inactivos en todos.
        compiled_somewhere = [False] * len(lines)
        for rep, syms in zip(reports, defines):
            if nf in rep:
                act = active_lines(lines, syms)
                compiled_somewhere = [x or y for x, y in zip(compiled_somewhere, act)]
        if PLATFORM_DIR.search(f) and not any(nf in rep for rep in reports):
            compiled_somewhere = [False] * len(lines)
        uncompiled_all = active_lines(lines, PLATFORMS)  # cualquier plataforma: cuenta todo lo que alguna compila
        any_platform = [x or y for x, y in zip(active_lines(lines, set()), uncompiled_all)]
        est = estimate(lines, lambda n: (not compiled_somewhere[n - 1]) and any_platform[n - 1])
        executable |= est
        total += len(executable)
        covered += len(hit & executable)
        rows.append((os.path.relpath(f, app), len(hit & executable), len(executable)))

    if a.detalle:
        for r, c, t in rows:
            if t:
                print(f"{c:5d} / {t:5d}  {100.0 * c / t:5.1f} %  {r}")
    if a.calibrar:
        ok, extra, miss = calib
        print(f"Calibracion: {ok} iguales, {extra} estimadas de mas, {miss} de menos "
              f"(error {100.0 * (extra + miss) / max(1, ok + miss):.1f} %)")
    pct = 100.0 * covered / total if total else 0.0
    print(f"Cobertura sobre toda la app (lineas ejecutables): {covered} de {total} lineas = {pct:.1f} %")
    return 0


if __name__ == "__main__":
    sys.exit(main())
