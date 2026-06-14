import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';

/**
 * Minimal local mirror of the bumpp config types.
 *
 * `bumpp` is provided by the Nix dev shell (see `nix/bumpp/`) rather than as a
 * pnpm dependency, so it cannot be imported here. We only model the surface of
 * the bumpp operation that this config touches.
 */
type BumppOperation = {
	options: { cwd: string };
	state: { newVersion: string; updatedFiles: string[] };
	update: (changes: { updatedFiles: string[] }) => void;
};

type BumppConfig = {
	execute: (operation: BumppOperation) => void | Promise<void>;
};

const RUST_RELEASE_FILE_PATTERN = /^rust\/(?:Cargo\.lock|crates\/[^/]+\/Cargo\.toml)$/;
const GIT_STATUS_FILE_PATTERN = /^.. (?<filePath>.+)$/;

function getUpdatedRustReleaseFiles(cwd: string): string[] {
	const result = spawnSync('git', ['status', '--short', '--', 'rust/Cargo.lock', 'rust/crates'], {
		cwd,
		encoding: 'utf8',
	});
	if (result.error != null) {
		throw result.error;
	}
	if (result.status !== 0) {
		throw new Error(`git status failed with exit code ${result.status ?? 'unknown'}`);
	}
	return result.stdout
		.split('\n')
		.map((line) => GIT_STATUS_FILE_PATTERN.exec(line)?.groups?.filePath)
		.filter(
			(filePath): filePath is string =>
				filePath != null && RUST_RELEASE_FILE_PATTERN.test(filePath),
		);
}

const config: BumppConfig = {
	async execute(operation) {
		const result = spawnSync(
			'cargo',
			[
				'set-version',
				'--manifest-path',
				'rust/Cargo.toml',
				'--workspace',
				operation.state.newVersion,
			],
			{
				cwd: operation.options.cwd,
				stdio: 'inherit',
			},
		);
		if (result.error != null) {
			throw result.error;
		}
		if (result.status !== 0) {
			throw new Error(`cargo set-version failed with exit code ${result.status ?? 'unknown'}`);
		}
		operation.update({
			updatedFiles: [
				...new Set([
					...operation.state.updatedFiles,
					...getUpdatedRustReleaseFiles(operation.options.cwd).map((filePath) =>
						resolve(operation.options.cwd, filePath),
					),
				]),
			],
		});
	},
};

export default config;
