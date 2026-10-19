#!/usr/bin/env node
// Builds Jellyfin.Plugin.DiscMenus/Web/editor/schema-hints.js: what the Menu Editor's forms need to know about each part of a menu (its
// fields, their types, allowed values and limits), read from schema/menu.schema.json so a change to the schema reaches the forms, plus the
// few things a schema can't say (labels, defaults, which field applies to which background source...) from tools/schema-hints/overrides.json.
//
//   node tools/schema-hints/generate.js           write the file
//   node tools/schema-hints/generate.js --check   fail if the file on disk differs (CI runs this, so the two can't drift apart)
//
// Every name in overrides.json has to exist in the schema, so a renamed field is an error here, not a silently missing form field.
'use strict';
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..', '..');
const SCHEMA = JSON.parse(fs.readFileSync(path.join(ROOT, 'schema/menu.schema.json'), 'utf8'));
const OVERRIDES = JSON.parse(fs.readFileSync(path.join(__dirname, 'overrides.json'), 'utf8'));
const OUT = path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/editor/schema-hints.js');

// $defs that are small value types, not parts of a menu: a field using one records it as `format`.
const FORMATS = ['color', 'label', 'key', 'imageRef', 'audioRef', 'percent', 'volume'];
// The kind (form) name for each object $def, and for objects written inline in the schema.
const KIND_OF_DEF = { menuLayout: 'layout', extraSpec: 'extra' };
const INLINE_KINDS = { 'match.providerIds': 'providerIds', 'match.release': 'release', 'audio.music': 'music', 'audio.sounds': 'sounds' };

const problems = [];
const kinds = {};

function defName(ref) {
    const m = /^#\/\$defs\/(.+)$/.exec(ref);
    if (!m) {
        throw new Error('unsupported $ref ' + ref);
    }

    return m[1];
}

// A schema node with its $ref followed (the node's own keys win over the target's) and the name of the def it came from.
function resolve(node) {
    if (node && typeof node === 'object' && node.$ref) {
        const name = defName(node.$ref);
        const target = SCHEMA.$defs[name];
        if (!target) {
            throw new Error('missing $def ' + name);
        }

        const own = Object.assign({}, node);
        delete own.$ref;
        return { node: Object.assign({}, target, own), def: name };
    }

    return { node: node, def: null };
}

function constraints(node, into) {
    ['minimum', 'maximum', 'exclusiveMinimum', 'minLength', 'maxLength', 'pattern', 'minItems', 'maxItems', 'minProperties'].forEach((k) => {
        if (node[k] !== undefined) {
            into[k] = node[k];
        }
    });
    if (node.description) {
        into.description = node.description;
    }
}

function kindNameFor(def, inlineKey) {
    if (def && KIND_OF_DEF[def]) {
        return KIND_OF_DEF[def];
    }

    if (def) {
        return def;
    }

    if (INLINE_KINDS[inlineKey]) {
        return INLINE_KINDS[inlineKey];
    }

    throw new Error('an object in the schema has no kind name: ' + inlineKey + ' (add it to INLINE_KINDS)');
}

function field(name, rawSchema, required, parentKey) {
    const { node, def } = resolve(rawSchema);
    const f = { name: name };
    if (required) {
        f.required = true;
    }

    if (node.enum) {
        f.type = 'enum';
        f.enum = node.enum.slice();
    } else if (node.type === 'object' && node.properties) {
        f.type = 'object';
        f.kind = kindNameFor(def, parentKey + '.' + name);
        buildKind(f.kind, node, parentKey + '.' + name);
        if (Array.isArray(node.required) && rawSchema.required) {
            f.requireAll = node.required.slice();
        }
    } else if (node.type === 'object' && node.additionalProperties && typeof node.additionalProperties === 'object') {
        const item = resolve(node.additionalProperties);
        f.type = 'map';
        f.kind = kindNameFor(item.def, name);
        buildKind(f.kind, item.node, name);
    } else if (node.type === 'array') {
        f.type = 'array';
        const item = resolve(node.items);
        if (item.node.type === 'object' || item.node.oneOf) {
            f.kind = kindNameFor(item.def, name);
            buildKind(f.kind, item.node, name);
        } else {
            f.itemType = item.node.type;
            if (item.def && FORMATS.includes(item.def)) {
                f.itemFormat = item.def;
            }
        }
    } else {
        f.type = node.type;
        if (!f.type) {
            throw new Error('field ' + parentKey + '.' + name + ' has no type');
        }

        if (def && FORMATS.includes(def)) {
            f.format = def;
        }
    }

    constraints(node, f);
    return f;
}

function buildKind(name, node, key) {
    if (kinds[name] && kinds[name].built) {
        return;
    }

    const kind = { fields: [] };
    kinds[name] = Object.assign({ built: true }, kind);
    let byName = new Map();
    const add = (propName, schema, required) => {
        if (schema === true || (schema && schema.const !== undefined)) {
            return; // the discriminator's own const and "label: true" placeholders are not fields of their own
        }

        if (!byName.has(propName)) {
            byName.set(propName, field(propName, schema, required, name));
        }
    };

    const baseRequired = new Set(node.required || []);
    Object.keys(node.properties || {}).forEach((p) => {
        const s = node.properties[p];
        if (s && s.enum && Array.isArray(node.oneOf)) {
            add(p, s, baseRequired.has(p)); // the discriminator (action, type): an enum on the base object
        } else {
            add(p, s, baseRequired.has(p));
        }
    });

    if (Array.isArray(node.oneOf)) {
        const discriminator = Object.keys(node.properties || {}).find((p) => node.properties[p].enum) || 'type';
        kind.discriminator = discriminator;
        kind.variants = {};
        node.oneOf.forEach((variant) => {
            const value = variant.properties[discriminator].const;
            const required = new Set(variant.required || []);
            const fields = [];
            Object.keys(variant.properties).forEach((p) => {
                if (p === discriminator) {
                    return;
                }

                const s = variant.properties[p];
                if (s === true) {
                    return;
                }

                add(p, s, required.has(p) || baseRequired.has(p));
                fields.push(p);
            });
            // base properties that are required for every variant (layer: type, position)
            node.required && node.required.forEach((p) => { if (p !== discriminator && !fields.includes(p) && node.properties[p] !== true && node.properties[p]) { fields.push(p); } });
            kind.variants[value] = { title: variant.title || value, fields: fields, required: Array.from(new Set([...required, ...(node.required || [])].filter((p) => p !== discriminator))) };
        });
    }

    if (Array.isArray(node.allOf)) {
        // "when source is X, field Y is required"
        const when = {};
        node.allOf.forEach((rule) => {
            if (rule.if && rule.then && rule.then.required) {
                const prop = Object.keys(rule.if.properties)[0];
                when.field = prop;
                when.values = when.values || {};
                when.values[rule.if.properties[prop].const] = rule.then.required.slice();
            }
        });
        if (when.field) {
            kind.requiredWhen = when;
        }
    }

    if (kind.discriminator && !byName.has(kind.discriminator)) {
        // the discriminator is only a const inside each variant (layer: type): give it a field of its own
        const own = { name: kind.discriminator, type: "enum", enum: Object.keys(kind.variants), required: true };
        byName = new Map([[kind.discriminator, own], ...byName]);
    }

    kind.fields = Array.from(byName.values());
    delete kinds[name].built;
    kinds[name] = kind;
    kinds[name].built = true;
}

function applyOverrides() {
    Object.keys(OVERRIDES.kinds || {}).forEach((kindName) => {
        const kind = kinds[kindName];
        const o = OVERRIDES.kinds[kindName];
        if (!kind) {
            problems.push('overrides.json: no kind "' + kindName + '" in the schema');
            return;
        }

        const names = kind.fields.map((f) => f.name);
        (o.order || []).forEach((n) => { if (!names.includes(n)) { problems.push('overrides.json: ' + kindName + '.order names missing field "' + n + '"'); } });
        (o.basic || []).forEach((n) => { if (!names.includes(n)) { problems.push('overrides.json: ' + kindName + '.basic names missing field "' + n + '"'); } });
        Object.keys(o.fields || {}).forEach((n) => {
            const target = kind.fields.find((f) => f.name === n);
            if (!target) {
                problems.push('overrides.json: ' + kindName + ' has no field "' + n + '"');
                return;
            }

            Object.assign(target, o.fields[n]);
        });
        if (o.order) {
            const rank = (n) => { const i = o.order.indexOf(n); return i < 0 ? 1000 : i; };
            kind.fields.sort((a, b) => rank(a.name) - rank(b.name));
        }

        ['title', 'create', 'hidden', 'basic', 'moreLabel', 'moreAuto', 'hint'].forEach((k) => { if (o[k] !== undefined) { kind[k] = o[k]; } });
        Object.keys(kind.variants || {}).forEach((v) => {
            const vo = (o.variants || {})[v];
            if (vo) {
                Object.assign(kind.variants[v], vo);
            }
        });
        Object.keys(o.variants || {}).forEach((v) => { if (!kind.variants || !kind.variants[v]) { problems.push('overrides.json: ' + kindName + ' has no variant "' + v + '"'); } });
    });
}

buildKind('document', SCHEMA, 'document');
Object.keys(kinds).forEach((k) => { delete kinds[k].built; });
applyOverrides();

if (problems.length) {
    console.error(problems.join('\n'));
    process.exit(1);
}

const ordered = {};
Object.keys(kinds).sort().forEach((k) => { ordered[k] = kinds[k]; });
const body = JSON.stringify({ kinds: ordered }, null, 2);
const output = `/*
 * GENERATED by tools/schema-hints/generate.js from schema/menu.schema.json and tools/schema-hints/overrides.json. Do not edit by hand:
 * change the schema or the overrides and run \`node tools/schema-hints/generate.js\` (CI fails if this file is stale).
 *
 * What the Menu Editor's forms know about each part of a menu: its fields, their types, allowed values and limits.
 */
(function (root, factory) {
    var api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.DiscMenusSchemaHints = api;
    }
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';
    return ${body.replace(/\n/g, '\n    ')};
});
`;

if (process.argv.includes('--check')) {
    const current = fs.existsSync(OUT) ? fs.readFileSync(OUT, 'utf8') : '';
    if (current !== output) {
        console.error('schema-hints.js is out of date: run `node tools/schema-hints/generate.js` and commit the result.');
        process.exit(1);
    }

    console.log('schema-hints.js is up to date.');
} else {
    fs.writeFileSync(OUT, output);
    console.log('wrote ' + path.relative(ROOT, OUT) + ' (' + Object.keys(ordered).length + ' kinds)');
}
