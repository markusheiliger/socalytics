import { useTranslation } from 'react-i18next';
import { isCurrent, type Registration } from '../../platform/models/registrations';

/** Connectivity, operating state, Analysts, and version of a current registration; ended registrations show none (US4 scenario 4). */
export function StatusLines({ r, showVersion = true }: { r: Registration; showVersion?: boolean }) {
  const { t } = useTranslation('managers');
  const current = isCurrent(r);
  const status = r.status;
  const when = (value?: Date) => value?.toLocaleString();
  const connectivity = r.connectivity === undefined ? undefined : t(`connectivity.${r.connectivity}`);
  const version = status?.managerVersion ?? r.deviceMetadata.managerVersion;

  let operating: string | undefined;
  if (status) {
    if (status.operatingState === 'paused' && status.pausedUntil) {
      operating = t('operating.pausedUntil', { value: when(status.pausedUntil) });
    } else if (status.operatingState === 'runtimeUnavailable' && status.runtimeUnavailableReason) {
      operating = t('operating.runtimeUnavailableReason', { reason: status.runtimeUnavailableReason });
    } else {
      operating = t(`operating.${status.operatingState}`);
    }
  }

  let analysts: string | undefined;
  if (status && status.activeAnalysts !== undefined && status.maxAnalysts !== undefined) {
    analysts = t('fleet.analysts', { active: status.activeAnalysts, max: status.maxAnalysts });
  } else if (status && status.activeAnalysts !== undefined) {
    analysts = t('fleet.analystsActiveOnly', { active: status.activeAnalysts });
  } else if (status && status.maxAnalysts !== undefined) {
    analysts = t('fleet.analystsMaxOnly', { max: status.maxAnalysts });
  }

  return (
    <>
      {current && connectivity !== undefined && (
        <li>
          {status?.reportedAt
            ? t('fleet.connectivityWithReport', { value: connectivity, time: when(status.reportedAt) })
            : t('fleet.connectivityNoReport', { value: connectivity })}
        </li>
      )}
      {current && operating && <li>{t('fleet.operatingState', { value: operating })}</li>}
      {current && analysts && <li>{analysts}</li>}
      {showVersion && version && <li>{t('fleet.version', { value: version })}</li>}
    </>
  );
}
