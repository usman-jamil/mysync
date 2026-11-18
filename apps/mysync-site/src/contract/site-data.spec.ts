import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import Ajv2020 from 'ajv/dist/2020';
import addFormats from 'ajv-formats';
import { fixture } from '../testing/fixture';
import { loadSiteData, type SiteData } from './site-data';

const schema = JSON.parse(readFileSync(resolve(__dirname, '../../contract/site-data.schema.json'), 'utf8'));

function validator() {
  const ajv = new Ajv2020({ allErrors: true, strict: true });
  addFormats(ajv);
  return ajv.compile(schema);
}

describe('the data contract', () => {
  const validate = validator();

  it('accepts the committed dev fixture (public/site-data.js)', () => {
    const valid = validate(fixture());
    expect(validate.errors ?? []).toEqual([]);
    expect(valid).toBe(true);
  });

  it('keeps the fixture in ordinal path order, as mysync writes it', () => {
    const paths = fixture().entries.map((entry) => entry.path);
    expect(paths).toEqual([...paths].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0)));
  });

  it('totals match the entries', () => {
    const data = fixture();
    const files = data.entries.filter((entry) => entry.kind === 'file');
    expect(data.totals.files).toBe(files.length);
    expect(data.totals.bytes).toBe(files.reduce((sum, entry) => sum + entry.size, 0));
  });

  const aFile = (d: SiteData) => d.entries.find((entry) => entry.kind === 'file');
  it.each<[string, (d: SiteData) => unknown]>([
    ['a newer schema version', (d) => ({ ...d, schemaVersion: 2 })],
    ['an absolute path', (d) => ({ ...d, entries: [{ kind: 'dir', path: '/etc' }] })],
    ['a parent segment', (d) => ({ ...d, entries: [{ kind: 'dir', path: 'a/../b' }] })],
    ['a backslash', (d) => ({ ...d, entries: [{ kind: 'dir', path: 'a\\b' }] })],
    ['an unknown disposition', (d) => ({ ...d, entries: [{ ...aFile(d), disposition: 'run' }] })],
    ['an extra property', (d) => ({ ...d, shareId: 'x' })],
    ['a short hash', (d) => ({ ...d, entries: [{ ...aFile(d), sha256: 'abc' }] })],
  ])('rejects %s', (_name, change) => {
    expect(validate(change(fixture()))).toBe(false);
  });
});

describe('loadSiteData', () => {
  it('loads the fixture', () => {
    expect(loadSiteData(fixture())).toMatchObject({ ok: true });
  });

  it('says when site-data.js is missing', () => {
    expect(loadSiteData(undefined)).toMatchObject({ ok: false, reason: 'missing' });
  });

  it('refuses a schema version it does not know', () => {
    expect(loadSiteData({ ...fixture(), schemaVersion: 2 })).toMatchObject({ ok: false, reason: 'unsupported' });
  });

  it.each([
    ['a string', 'nope'],
    ['no entries', { ...fixture(), entries: undefined }],
    ['a broken entry', { ...fixture(), entries: [{ kind: 'file', path: 'a' }] }],
    ['an empty path', { ...fixture(), entries: [{ kind: 'dir', path: '' }] }],
  ])('refuses %s', (_name, value) => {
    expect(loadSiteData(value)).toMatchObject({ ok: false, reason: 'invalid' });
  });
});
