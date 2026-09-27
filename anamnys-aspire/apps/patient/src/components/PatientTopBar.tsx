import AccountMenu from "@anamnys/shared/ui/AccountMenu";
import logo from "@anamnys/shared/assets/logo1.png";

// The patient portal's only chrome: there are no patient sections to navigate between
// yet, so no Sidenav — logo left, account menu (name, email, Sair) right.
export default function PatientTopBar() {
  return (
    <header className="sticky top-0 z-30 bg-surface border-b border-surfaceVariant px-4 md:px-6">
      <div className="h-16 max-w-5xl mx-auto flex items-center justify-between">
        <img src={logo} alt="Anamnys" width={140} height={30} className="w-[140px] h-[30px] object-contain" />
        <AccountMenu realm="patient" />
      </div>
    </header>
  );
}
