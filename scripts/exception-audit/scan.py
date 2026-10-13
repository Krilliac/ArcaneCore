import re, sys, os, json

BS = chr(92)
root = sys.argv[1]
out = []
catch_re = re.compile(r'\bcatch\b')


def mask(t):
    m = list(t)
    i = 0
    n = len(t)
    while i < n:
        c = t[i]
        if c == '/' and i + 1 < n and t[i + 1] == '/':
            j = t.find('\n', i)
            j = n if j < 0 else j
            for k in range(i, j):
                m[k] = ' '
            i = j
        elif c == '/' and i + 1 < n and t[i + 1] == '*':
            j = t.find('*/', i + 2)
            j = n if j < 0 else j + 2
            for k in range(i, j):
                if t[k] != '\n':
                    m[k] = ' '
            i = j
        elif c == '"':
            if t.startswith('"""', i):
                j = t.find('"""', i + 3)
                j = n if j < 0 else j + 3
                for k in range(i, j):
                    if t[k] != '\n':
                        m[k] = ' '
                i = j
            else:
                verb = (i > 0 and t[i - 1] == '@') or (i > 1 and t[i - 2:i] in ('$@', '@$'))
                j = i + 1
                while j < n:
                    if verb:
                        if t[j] == '"':
                            if j + 1 < n and t[j + 1] == '"':
                                j += 2
                                continue
                            break
                        j += 1
                    else:
                        if t[j] == BS:
                            j += 2
                            continue
                        if t[j] == '"' or t[j] == '\n':
                            break
                        j += 1
                for k in range(i + 1, min(j, n)):
                    if t[k] != '\n':
                        m[k] = ' '
                i = j + 1
        elif c == "'":
            j = i + 1
            if j < n and t[j] == BS:
                j += 2
            else:
                j += 1
            while j < n and t[j] != "'" and t[j] != '\n':
                j += 1
            for k in range(i + 1, min(j, n)):
                m[k] = ' '
            i = j + 1
        else:
            i += 1
    return ''.join(m)


for dp, dn, fn in os.walk(os.path.join(root, 'src')):
    dn[:] = [d for d in dn if d not in ('obj', 'bin')]
    for f in fn:
        if not f.endswith('.cs'):
            continue
        p = os.path.join(dp, f)
        t = open(p, encoding='utf-8-sig').read()
        mt = mask(t)
        n = len(t)
        for mm in catch_re.finditer(mt):
            j = mm.end()
            while j < n and mt[j].isspace():
                j += 1
            decl = ''
            var = None
            typ = '(all)'
            if j < n and mt[j] == '(':
                d = 1
                k = j + 1
                while k < n and d:
                    d += (mt[k] == '(') - (mt[k] == ')')
                    k += 1
                decl = t[j:k]
                j = k
                mv = re.match(r'\(\s*([\w.<>]+)\s*(\w+)?\s*\)', decl)
                if mv:
                    typ = mv.group(1)
                    var = mv.group(2)
                while j < n and mt[j].isspace():
                    j += 1
            if mt.startswith('when', j):
                k = mt.find('(', j)
                d = 1
                k += 1
                while k < n and d:
                    d += (mt[k] == '(') - (mt[k] == ')')
                    k += 1
                decl += ' ' + t[j:k]
                j = k
                while j < n and mt[j].isspace():
                    j += 1
            if j < n and mt[j] == '{':
                d = 1
                k = j + 1
                while k < n and d:
                    d += (mt[k] == '{') - (mt[k] == '}')
                    k += 1
                body = t[j:k]
            else:
                k = mt.find(';', j)
                body = t[j:k + 1]
            line = t.count('\n', 0, mm.start()) + 1
            out.append(dict(file=os.path.relpath(p, root).replace(BS, '/'), line=line, typ=typ, var=var, decl=decl.strip(), body=body))
json.dump(out, open(sys.argv[2], 'w'))
print(len(out))
