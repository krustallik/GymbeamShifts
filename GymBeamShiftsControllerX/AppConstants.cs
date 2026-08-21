namespace GymBeamShiftsControllerX
{
    public static class AppConstants
    {
        public const string ConfigFileName = "appconfig.json";
        public const string LogFileName = "log.txt";

        public const string LoginFieldName = "login";
        public const string PasswordFieldName = "password";
        public const string SubmitButtonSelector = "button[type='submit']";

        public const string InvitationsTableLengthName = "invitations_table_length";
        public const string SortHeaderSelector = "#invitations_table thead th:nth-child(2)";
        public const string TableRowsSelector = "#invitations_table tbody tr";
        public const string SubscribeButtonSelector = "button.subscribe_shift";
        public const string ActivePaginationPageSelector = "#invitations_table_paginate li.active a";
        public const string PreviousPaginationButtonId = "invitations_table_previous";
        public const string NextPaginationButtonId = "invitations_table_next";

        public const string CookiesEssentialButtonId = "cookies-consent-essential";

        public const string SubscribeModalId = "modal_subscribe";
        public const string NewWorkersNoticeText = "Noví brigádnici";
        public const string SubscribeModalDismissSelector = "[data-bs-dismiss='modal'], [data-dismiss='modal'], .btn-close, .close";
        public const string LunchNoRadioId = "lunch_no";
        public const string LunchYesRadioId = "lunch_yes";
        public const string SubscribeSubmitButtonId = "subscribe_submit";
    }
}
