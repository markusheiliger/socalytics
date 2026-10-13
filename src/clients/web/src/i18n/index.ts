import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import common from './en/common.json';
import session from './en/session.json';

void i18n.use(initReactI18next).init({
  lng: 'en',
  fallbackLng: 'en',
  defaultNS: 'common',
  ns: ['common', 'session'],
  resources: { en: { common, session } },
  interpolation: { escapeValue: false },
});

export default i18n;
